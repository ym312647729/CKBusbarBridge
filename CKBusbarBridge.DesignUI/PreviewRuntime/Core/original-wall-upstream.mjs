import {DefinitionError} from './expressions.mjs';
import {buildDesignExport} from './design-export.mjs';
import {deriveCabinetScene} from './cabinet-scene.mjs';
import {readUpstreamStructure} from './upstream-structure-geometry.mjs';
import {readUpstreamInsulatorConnections} from './upstream-insulator-geometry.mjs';
import {upstreamPlanarBodies} from './upstream-planar-faces.mjs';
import {wallBushingHoleSources} from './wall-bushing-preview.mjs';
import {evaluatePreview} from './preview-quality.mjs';
import {swMatrix,inverseRigidMatrix,multiplyMatrix,transformPoint} from './geometry.mjs';
const one=(rows,label)=>{if(rows.length!==1)throw new DefinitionError(label+'必须唯一，实际'+rows.length);return rows[0];};
const cross=(a,b)=>[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]],unit=vector=>vector.map(value=>value/Math.hypot(...vector)),near=(a,b)=>Math.hypot(...a.map((value,index)=>value-b[index]))<.01;

export async function buildOriginalWallUpstream(project,read,{kernel,readWallConductors}={}){
  if(!kernel||typeof readWallConductors!=='function')throw new DefinitionError('面对墙需要纯几何内核与原套管I排识别入口');
  const clean=structuredClone(project);delete clean.frameDesign;delete clean.originalDesign;clean.objects=clean.objects.filter(object=>!object.frameBinding&&!object.structureSupportBinding&&!object.originalDesignBinding);clean.designModules={cabinet:true};
  const design=(await buildDesignExport(clean,{readTemplate:read})).design,record=design.inputs,values=record.values;
  if(values['Cabinet.FrontHole.IsEnabled']===values['Cabinet.BackHole.IsEnabled'])throw new DefinitionError('面对墙必须且只能启用一个柜顶孔');
  const position=values['Cabinet.FrontHole.IsEnabled']?'柜前':'柜后',base=await readUpstreamStructure(one(design.bases.models.filter(row=>row.kind==='Base'&&row.positionName===position),'面对墙普通基座'),design,read),port=base.point('基座安装坐标系'),along=unit(base.pose.slice(0,3)),up=unit(base.pose.slice(4,7)),coordinates={origin:[port[0],0,port[2]],along,up,across:unit(cross(along,up))};
  const cabinetSource=await read(design.cabinet.templatePath),derived=await deriveCabinetScene(cabinetSource,design.cabinet.configuration,'face-to-wall',record,read),root=derived.template.configurations.find(row=>row.name===design.cabinet.configuration),group=one(design.cabinet.groups,'面对墙柜体参数组'),component=one(root.components.filter(row=>row.name===group.instancePath[0]),'面对墙柜实例'),child=(await derived.loadTemplate(component.path)).configurations.find(row=>row.name===component.configuration),body=one(child.components.filter(row=>row.name===group.instancePath[1]),'面对墙柜体'),bodyConfig=(await derived.loadTemplate(body.path)).configurations.find(row=>row.name===body.configuration),profile=one(bodyConfig.features.filter(row=>row.name==='草图1'&&row.sketch),'柜体矩形轮廓'),extrusion=one(bodyConfig.features.filter(row=>row.name==='凸台-拉伸1'),'柜体单向拉伸').definition.properties;
  const segments=profile.sketch.segments.filter(row=>!row.construction),depth=values['Cabinet.CabinetDepth'],height=values['Cabinet.CabinetHeight'],width=values['Cabinet.CabinetWidth'];
  if(segments.length!==4||segments.some(row=>row.type!==0)||extrusion.ReverseDirection||Math.abs(extrusion.forwardDepth*1000-width)>.01||Math.abs(extrusion.reverseDepth*1000)>.01)throw new DefinitionError('面对墙柜体外形或拉伸与原模板关系不一致');
  const profilePoints=segments.flatMap(row=>[row.geometry.start,row.geometry.end]).map(point=>transformPoint(point.map(value=>value*1000),inverseRigidMatrix(swMatrix(profile.sketch.modelToSketch))));
  for(const x of [-depth/2,depth/2])for(const y of [-height/2,height/2])if(profilePoints.filter(point=>near(point,[x,y,0])).length!==2)throw new DefinitionError('面对墙柜体外形轮廓未复现原坐标关系');
  const globals=base.model.globalVariables,localPort=base.coordinate('基座安装坐标系').slice(12,15),nominals=[{id:'wall-cabinet',name:'柜体',kind:'Cabinet',pose:multiplyMatrix(swMatrix(component.transform),swMatrix(body.transform)),center:[0,0,width/2],dimensions:[depth,height,width]},{id:'wall-base',name:'普通基座',kind:'Base',pose:base.pose,center:[localPort[0]-globals['基座深']/2,localPort[1]-(globals['基座高']-globals['基座开孔高度'])/2,localPort[2]],dimensions:[globals['基座深'],globals['基座高'],globals['基座宽']]}];
  const raisedModel=design.bases.models.find(row=>row.kind==='ElevatedBase'&&row.positionName===position),raised=raisedModel?await readUpstreamStructure(raisedModel,design,read):null;
  if(raised){const lower=raised.coordinate('下'),upper=raised.coordinate('上'),g=raised.model.globalVariables;
    for(const [matrix,x]of [[lower,g['高度']/2],[upper,-g['高度']/2]])if([0,1,0,-1,0,0,0,0,1].some((value,index)=>Math.abs(value-matrix[[0,1,2,4,5,6,8,9,10][index]])>1e-6)||Math.abs(matrix[12]-x)>.01||Math.abs(matrix[13]-(-g['深度']/2+21.5))>.01||Math.abs(matrix[14])>.01)throw new DefinitionError('增高基座上下安装坐标与原模板关系不一致');
    nominals.push({id:'wall-raised-base',name:'增高基座',kind:'Base',pose:raised.pose,center:[(lower[12]+upper[12])/2,(lower[13]+upper[13])/2+g['深度']/2-21.5,(lower[14]+upper[14])/2],dimensions:[g['高度'],g['深度'],g['宽度']]});}
  const host=one(design.bases.models.filter(row=>row.positionName===position&&row.insulators.length),'有效预装绝缘子宿主'),connections=await readUpstreamInsulatorConnections(host,design,read,kernel);if(connections.length!==3)throw new DefinitionError('面对墙柜端必须有三相实际配合面');
  const wall=one(root.components.filter(row=>row.name==='墙-1'),'面对墙墙体'),wallConfig=(await derived.loadTemplate(wall.path)).configurations.find(row=>row.name===wall.configuration),openingProfile=one(wallConfig.features.filter(row=>row.name==='草图2'&&row.sketch),'墙洞矩形轮廓'),openingSegments=openingProfile.sketch.segments.filter(row=>!row.construction);
  if(openingSegments.length!==4||openingSegments.some(row=>row.type!==0))throw new DefinitionError('墙洞不是原四边矩形开孔');
  const wallPose=multiplyMatrix(swMatrix(wall.transform),inverseRigidMatrix(swMatrix(openingProfile.sketch.modelToSketch))),wallOpeningCorners=[];
  for(const point of openingSegments.flatMap(row=>[row.geometry.start,row.geometry.end]).map(point=>transformPoint(point.map(value=>value*1000),wallPose)))if(!wallOpeningCorners.some(other=>near(point,other)))wallOpeningCorners.push(point);
  if(wallOpeningCorners.length!==4)throw new DefinitionError('墙洞角点没有唯一对应原矩形');
  const components=[],bushingHoles=[];
  for(const placement of design.cabinet.bushing.placements){
    const installed=one(root.components.filter(row=>row.id===placement.componentId),'套管实际实例'),template=await derived.loadTemplate(installed.path),result=await evaluatePreview(kernel,template,installed.configuration,{}, {...project.settings,arcSegments:24},derived.loadTemplate);
    bushingHoles.push(...wallBushingHoleSources({template},one(design.cabinet.bushing.models.filter(model=>model.modelId===placement.modelId),'套管孔源模型'),swMatrix(installed.transform),placement.componentId));
    try{components.push({name:installed.name,bodies:upstreamPlanarBodies(result.solids,swMatrix(installed.transform))});}finally{result.solids.forEach(solid=>solid.delete());}
  }
  const wallConductors=await readWallConductors({components});if(!Array.isArray(wallConductors)||wallConductors.length<3)throw new DefinitionError('原套管识别没有返回三相实际I排');
  const specification=/^\s*(\d+(?:\.\d+)?)\s*[xX×*]\s*(\d+(?:\.\d+)?)\s*$/.exec(values['General.BusbarSpec']);if(!specification)throw new DefinitionError('面对墙原铜排规格无效');
  return {version:'original-design-upstream/1',scenario:'face-to-wall',modelName:project.name||'母线桥',rootCount:values['General.SinglePhaseRootCount'],coordinates,nominals,sides:[{side:'Cabinet',opening:port,openingHeight:globals['基座开孔高度'],baseWidth:globals['基座宽'],insulatorWithBracketHeight:values['General.InsulatorHeight'],elevatedOrdinaryJoint:raised?.point('上')??null,phaseSequence:'ABC',ordinaryConnections:connections,elevatedConnections:[],extensions:derived.fixedCopper.bars.filter(row=>row.positionName===position)}],wallOpeningCorners,wallConductors,bushingHoles,position,wallWidth:values['General.WallHoleWidth'],wallHeight:values['General.WallHoleHeight'],wallCabinetEccentricity:values['General.WallCabinetEccentricity'],overlapLength:Number(specification[1]),clearance:values['General.InsulationDistance'],isPhaseSwitched:values['General.IsPhaseSwitched'],wallInstallation:Object.fromEntries(['Top','Bottom','Left','Right','EntryDistance'].map(name=>[name,values['General.WallInstallation.'+name]])),fields:[{name:'面对墙有效孔位',value:position,source:'原柜体启用孔'},{name:'原铜排规格',value:values['General.BusbarSpec'],source:'原柜体参数'}],source:'original facing-wall upstream / driven datums / original I-bar recognition'};
}
