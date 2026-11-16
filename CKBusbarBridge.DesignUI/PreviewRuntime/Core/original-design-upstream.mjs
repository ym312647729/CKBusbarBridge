import {DefinitionError} from './expressions.mjs';
import {buildDesignExport} from './design-export.mjs';
import {deriveCabinetScene} from './cabinet-scene.mjs';
import {driveStructureCoordinates} from './structure-coordinates.mjs';
import {nativeSceneToPreview} from './structure-installation-preview.mjs';
import {baseInsulatorInput} from './insulator-design.mjs';
import {deriveInsulatorAssembly} from './insulator-preview.mjs';
import {swMatrix,inverseRigidMatrix,multiplyMatrix,transformPoint} from './geometry.mjs';
import {buildOriginalSideUpstream} from './original-side-upstream.mjs';
import {buildOriginalWallUpstream} from './original-wall-upstream.mjs';
const one=(rows,label)=>{if(rows.length!==1)throw new DefinitionError(label+'必须唯一，实际'+rows.length);return rows[0];};
const direction=(vector,matrix)=>transformPoint(vector,matrix).map((value,index)=>value-matrix[12+index]);
const unit=vector=>vector.map(value=>value/Math.hypot(...vector));

// Replaces the original upstream COM reads with already captured, driven DTOs.
// All bridge planning is performed by the original C# provider/editor afterwards.
export async function buildOriginalDesignUpstream(project,read,options={}){
  if(project.scenario==='side-to-side')return buildOriginalSideUpstream(project,read,options);
  if(project.scenario==='face-to-wall')return buildOriginalWallUpstream(project,read,options);
  if(project.scenario!=='face-to-face')throw new DefinitionError('该场景的原联合设计输入适配仍在迁移');
  const clean=structuredClone(project);delete clean.frameDesign;delete clean.originalDesign;
  clean.objects=clean.objects.filter(object=>!object.frameBinding&&!object.structureSupportBinding&&!object.originalDesignBinding);clean.designModules={cabinet:true};
  const design=(await buildDesignExport(clean,{readTemplate:read})).design,record=design.inputs,values=record.values;
  const cabinetSource=await read(design.cabinet.templatePath),derived=await deriveCabinetScene(cabinetSource,design.cabinet.configuration,project.scenario,record,read),root=derived.template.configurations.find(row=>row.name===design.cabinet.configuration);
  const fields=[],sides=[],number=(name,value,source)=>{if(!Number.isFinite(value))throw new DefinitionError(name+'缺少受驱数值');fields.push({name,value:String(value)+' mm',source});},point=(name,value,source)=>value.forEach((component,index)=>number(name+'XYZ'[index],component,source));
  const nominal=(label,center,length,width,height,source)=>{point(label+'名义中心',center,source);number(label+'名义X向尺寸',length,source);number(label+'名义Y向尺寸',height,source);number(label+'名义Z向尺寸',width,source);};
  for(const side of ['B','A']){
    const prefix=side+'Cabinet',group=one(design.cabinet.groups.filter(row=>row.group===prefix),side+'柜参数组'),component=one(root.components.filter(row=>row.name===group.instancePath[0]),side+'柜实例'),child=await derived.loadTemplate(component.path),body=one(child.configurations.find(row=>row.name===component.configuration).components.filter(row=>row.name===group.instancePath[1]),side+'柜体实例');
    nominal(side+'柜',multiplyMatrix(swMatrix(component.transform),swMatrix(body.transform)).slice(12,15),values[prefix+'.CabinetDepth'],values[prefix+'.CabinetWidth'],values[prefix+'.CabinetHeight'],group.templatePath);
    const base=one(design.bases.models.filter(row=>row.positionName.startsWith(side)&&row.kind==='Base'),side+'柜启用基座'),pose=one(design.installation.preview.instances.filter(row=>row.instanceId===base.modelId),side+'基座安装位置'),nativePose=multiplyMatrix(inverseRigidMatrix(nativeSceneToPreview),pose.transform),source=await read(base.templatePath),sourceConfiguration=source.configurations.find(row=>row.name===base.configuration),configuration=structuredClone(sourceConfiguration),graph=one(base.structure.models.filter(row=>row.templatePath===base.templatePath&&row.configuration===base.configuration),'基座受驱尺寸');
    if(!driveStructureCoordinates(sourceConfiguration,configuration,graph))throw new DefinitionError('基座安装坐标未求值');
    const coordinate=name=>swMatrix(one(configuration.features.filter(row=>row.type==='CoordSys'&&row.name===name&&!row.suppressed),name).definition.properties.Transform.transform).slice(12,15),openingLocal=coordinate('基座安装坐标系'),bottom=transformPoint(coordinate('安装坐标系'),nativePose),opening=transformPoint(openingLocal,nativePose),globals=base.globalVariables;
    nominal(side+'柜基座',transformPoint([openingLocal[0]-globals['基座深']/2,openingLocal[1]-(globals['基座高']-globals['基座开孔高度'])/2,openingLocal[2]],nativePose),globals['基座深'],globals['基座宽'],globals['基座高'],base.templatePath);
    point(side+'柜基座底面原点',bottom,base.templatePath);point(side+'柜基座孔口中心',opening,base.templatePath);for(const [key,value]of Object.entries(globals))number(side+'柜基座 · '+key,value,base.templatePath);
    const host=one(design.bases.models.filter(row=>row.positionName===base.positionName&&row.insulators.length),'有效基座绝缘子宿主'),placement=one(host.insulators,'预装绝缘子组'),insulatorPose=one(design.installation.preview.instances.filter(row=>row.instanceId===placement.design.modelId),'绝缘子组安装位置'),nativeInsulatorPose=multiplyMatrix(inverseRigidMatrix(nativeSceneToPreview),insulatorPose.transform),assembly=await deriveInsulatorAssembly(baseInsulatorInput(placement),read),{recipe,sourceBody,childConfiguration,solution}=assembly,top=assembly.root.configurations.find(row=>row.name===recipe.configuration),face=one(sourceBody.configurations.find(row=>row.name===recipe.shared.body.configuration).features.filter(row=>row.name==='配合面外形1'&&row.sketch),'绝缘子配合面'),circle=one(face.sketch.segments.filter(row=>row.type===1&&!row.construction),'绝缘子配合圆'),local=transformPoint(circle.geometry.center.map(value=>value*1000),inverseRigidMatrix(swMatrix(face.sketch.modelToSketch))),normalLocal=face.sketch.support.entity.planeParameters.slice(0,3);
    local[1]*=assembly.heightRatio;
    const bodyComponent=one(childConfiguration.components.filter(row=>row.id===recipe.shared.body.componentId),'绝缘子本体'),bodyPose=swMatrix(bodyComponent.transform),movement=one(solution.translations.filter(row=>row.componentId===bodyComponent.id),'本体受驱平移');for(let axis=0;axis<3;axis++)bodyPose[12+axis]+=movement.delta[axis];
    const connections=recipe.groups.filter(row=>!row.suppressed).map(group=>{const groupPose=swMatrix(one(top.components.filter(row=>row.id===group.componentId),'绝缘子相组').transform);groupPose[14]=recipe.layout.groupLocalZ[group.group-1];const pose=multiplyMatrix(nativeInsulatorPose,multiplyMatrix(groupPose,bodyPose));return {name:base.modelId+':insulator:'+group.group,center:transformPoint(local,pose),normal:unit(direction(normalLocal,pose)),radius:circle.geometry.radius*1000,source:{templatePath:sourceBody.source.path,feature:face.name,featureId:face.id,group:group.group}};}).sort((a,b)=>a.center[2]-b.center[2]);
    const outgoing=derived.fixedCopper.bars.filter(row=>row.positionName===base.positionName);if(outgoing.length!==(values['General.SinglePhaseRootCount']-1)*3)throw new DefinitionError(side+'柜伸出排每相根数与E=N−1不一致');
    fields.push({name:side+'柜始端 · 相序',value:values['General.'+prefix+'ExposedBusbarPhaseSequence'],source:'原柜体参数'},{name:side+'柜始端 · 铜排规格',value:values['General.BusbarSpec'],source:'原柜体参数'});number(side+'柜始端 · 相间距',values['General.PhaseSpacing'],'原柜体参数');
    sides.push({side,opening,bottom,baseHeight:globals['基座高'],openingHeight:globals['基座开孔高度'],bodyHeight:recipe.shared.body.heightMm,connections,extensions:outgoing});
  }
  number('绝缘距离',values['General.InsulationDistance'],'原柜体参数');
  return {version:'original-design-upstream/1',scenario:project.scenario,modelName:project.name||'母线桥',rootCount:values['General.SinglePhaseRootCount'],fields,sides,source:'original upstream fields from driven sidecars; no native model reads'};
}
