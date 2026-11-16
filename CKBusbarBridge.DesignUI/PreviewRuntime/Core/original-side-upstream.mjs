import {DefinitionError} from './expressions.mjs';
import {buildDesignExport} from './design-export.mjs';
import {deriveCabinetScene} from './cabinet-scene.mjs';
import {driveStructureCoordinates} from './structure-coordinates.mjs';
import {nativeSceneToPreview} from './structure-installation-preview.mjs';
import {readUpstreamInsulatorConnections} from './upstream-insulator-geometry.mjs';
import {swMatrix,inverseRigidMatrix,multiplyMatrix,transformPoint} from './geometry.mjs';
const one=(rows,label)=>{if(rows.length!==1)throw new DefinitionError(label+'必须唯一，实际'+rows.length);return rows[0];};
const cross=(a,b)=>[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]],unit=vector=>vector.map(value=>value/Math.hypot(...vector));

// Data replacement for BusbarBridgeUpstreamModelReader.SideToSide. The original
// provider owns copper/support planning; all points below are driven datums.
export async function buildOriginalSideUpstream(project,read,{kernel}={}){
  const clean=structuredClone(project);delete clean.frameDesign;delete clean.originalDesign;
  clean.objects=clean.objects.filter(object=>!object.frameBinding&&!object.structureSupportBinding&&!object.originalDesignBinding);clean.designModules={cabinet:true};
  const design=(await buildDesignExport(clean,{readTemplate:read})).design,record=design.inputs,values=record.values,base1=values['General.CabinetTopOpeningMode']==='LeftRight';
  const fronts=['A','B'].map(side=>{const prefix=side+'Cabinet';if(values[prefix+'.FrontHole.IsEnabled']===values[prefix+'.BackHole.IsEnabled'])throw new DefinitionError(side+'柜必须且只能启用一个柜顶孔');return values[prefix+'.FrontHole.IsEnabled'];});
  if(fronts[0]!==fronts[1])throw new DefinitionError('侧对侧当前只允许前↔前、后↔后连接，不能交叉连接柜顶孔');
  const position=fronts[0]?'前':'后',cabinetSource=await read(design.cabinet.templatePath),derived=await deriveCabinetScene(cabinetSource,design.cabinet.configuration,project.scenario,record,read),root=derived.template.configurations.find(row=>row.name===design.cabinet.configuration),nominals=[],fields=[],sides=[];
  const fieldsPoint=(name,value,source)=>value.forEach((component,index)=>fields.push({name:name+'XYZ'[index],value:String(component)+' mm',source}));
  async function structure(model){
    const source=await read(model.templatePath),original=source.configurations.find(row=>row.name===model.configuration),configuration=structuredClone(original),graph=one(model.structure.models.filter(row=>row.templatePath===model.templatePath&&row.configuration===model.configuration),'基座受驱尺寸');
    if(!driveStructureCoordinates(original,configuration,graph))throw new DefinitionError('基座安装坐标未求值');
    const installed=one(design.installation.preview.instances.filter(row=>row.instanceId===model.modelId),'基座安装位置'),pose=multiplyMatrix(inverseRigidMatrix(nativeSceneToPreview),installed.transform),coordinate=name=>swMatrix(one(configuration.features.filter(row=>row.type==='CoordSys'&&row.name===name&&!row.suppressed),name).definition.properties.Transform.transform);
    return {model,pose,coordinate,point:name=>transformPoint(coordinate(name).slice(12,15),pose)};
  }
  const bases=await Promise.all(['A','B'].map(side=>structure(one(design.bases.models.filter(row=>row.positionName===side+'柜'+position&&row.kind===(base1?'Base1':'Base2')),side+'柜普通基座')))),bPort=bases[1].point('基座安装坐标系'),aPort=bases[0].point('基座安装坐标系'),along=unit(bases[1].pose.slice(0,3)),up=unit(bases[1].pose.slice(4,7));
  if(Math.abs(up[1]-1)>1e-6||aPort.reduce((sum,value,index)=>sum+(value-bPort[index])*along[index],0)<=0)throw new DefinitionError('侧对侧基座开口轴或竖直方向与模板关系不一致');
  const coordinates={origin:[bPort[0],0,bPort[2]],along,up,across:unit(cross(along,up))};
  for(const [index,side]of ['A','B'].entries()){
    const prefix=side+'Cabinet',group=one(design.cabinet.groups.filter(row=>row.group===prefix),side+'柜参数组'),component=one(root.components.filter(row=>row.name===group.instancePath[0]),side+'柜实例'),child=await derived.loadTemplate(component.path),body=one(child.configurations.find(row=>row.name===component.configuration).components.filter(row=>row.name===group.instancePath[1]),side+'柜体实例'),bodyPose=multiplyMatrix(swMatrix(component.transform),swMatrix(body.transform));
    nominals.push({id:'cabinet-'+side.toLowerCase(),name:side+'柜',kind:'Cabinet',pose:bodyPose,center:[0,0,0],dimensions:[values[prefix+'.CabinetDepth'],values[prefix+'.CabinetHeight'],values[prefix+'.CabinetWidth']]});
    const base=bases[index],globals=base.model.globalVariables,openingLocal=base.coordinate('基座安装坐标系').slice(12,15);
    nominals.push({id:'base-'+side.toLowerCase(),name:side+'侧普通基座',kind:'Base',pose:base.pose,center:[openingLocal[0]-globals['基座深']/2,openingLocal[1]-(globals['基座高']-globals['基座开孔高度'])/2,openingLocal[2]],dimensions:[globals['基座深'],globals['基座高'],globals['基座宽']]});
    let raised=null;
    if(!base1){
      raised=await structure(one(design.bases.models.filter(row=>row.positionName===base.model.positionName&&row.kind==='ElevatedBase'),side+'柜增高基座'));
      const lower=raised.coordinate('下'),upper=raised.coordinate('上'),height=raised.model.globalVariables['高度'],depth=raised.model.globalVariables['深度'],width=raised.model.globalVariables['宽度'];
      for(const [matrix,x]of [[lower,height/2],[upper,-height/2]]){
        if([0,1,0,-1,0,0,0,0,1].some((value,i)=>Math.abs(value-matrix[[0,1,2,4,5,6,8,9,10][i]])>1e-6)||Math.abs(matrix[12]-x)>.01||Math.abs(matrix[13]-(-depth/2+21.5))>.01||Math.abs(matrix[14])>.01)throw new DefinitionError('增高基座上下安装坐标与原模板关系不一致');
      }
      nominals.push({id:'elevated-'+side.toLowerCase(),name:side+'侧增高基座',kind:'Base',pose:raised.pose,center:[(lower[12]+upper[12])/2,(lower[13]+upper[13])/2+depth/2-21.5,(lower[14]+upper[14])/2],dimensions:[height,depth,width]});
    }
    const ordinaryConnections=await readUpstreamInsulatorConnections(base.model,design,read,kernel),elevatedConnections=raised?await readUpstreamInsulatorConnections(raised.model,design,read,kernel):[];
    if(base1&&ordinaryConnections.length!==6||!base1&&(ordinaryConnections.length!==0&&ordinaryConnections.length!==3||elevatedConnections.length!==3))throw new DefinitionError(side+'柜实际绝缘子组与原基座安装关系不符');
    const extensions=derived.fixedCopper.bars.filter(row=>row.positionName===base.model.positionName);
    if(extensions.length!==3*(values['General.SinglePhaseRootCount']-1))throw new DefinitionError(side+'柜伸出排每相根数与E=N−1不一致');
    fields.push({name:side+'柜始端 · 相序',value:values['General.'+prefix+'ExposedBusbarPhaseSequence'],source:'原柜体参数'},{name:side+'柜始端 · 铜排规格',value:values['General.BusbarSpec'],source:'原柜体参数'});
    fieldsPoint(side+'柜基座孔口中心',base.point('基座安装坐标系'),base.model.templatePath);
    sides.push({side,opening:base.point('基座安装坐标系'),openingHeight:globals['基座开孔高度'],baseWidth:globals['基座宽'],insulatorWithBracketHeight:values['General.InsulatorHeight'],elevatedOrdinaryJoint:raised?.point('上')??null,phaseSequence:values['General.'+prefix+'ExposedBusbarPhaseSequence'],ordinaryConnections,elevatedConnections,extensions});
  }
  if(Math.abs(sides[0].openingHeight-sides[1].openingHeight)>.001)throw new DefinitionError('侧对侧A/B柜同位置基架高度必须相同');
  const specification=/^\s*(\d+(?:\.\d+)?)\s*[xX×*]\s*(\d+(?:\.\d+)?)\s*$/.exec(values['General.BusbarSpec']);if(!specification)throw new DefinitionError('中间排规格缺少原排宽或板厚');
  return {version:'original-design-upstream/1',scenario:'side-to-side',modelName:project.name||'母线桥',rootCount:values['General.SinglePhaseRootCount'],fields,coordinates,nominals,sides,position,fixedPhaseChange:base1,cabinetEccentricity:values['General.CabinetEccentricity'],phaseSpacing:values['General.ExposedBusbarPhaseSpacing'],width:Number(specification[1]),thickness:Number(specification[2]),clearance:values['General.InsulationDistance'],frameInsulatorBodyHeight:Math.round((values['General.InsulatorHeight']-25+(base1?6:0))*1000)/1000,source:'original side-to-side upstream reader / driven sidecar datums'};
}
