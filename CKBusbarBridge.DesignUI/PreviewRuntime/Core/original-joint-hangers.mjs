import {DefinitionError} from './expressions.mjs';
import {validateProject} from './project.mjs';
import {buildDesignExport} from './design-export.mjs';
import {deriveCabinetScene} from './cabinet-scene.mjs';
import {collectOriginalRoofPlanes} from './original-roof-planes.mjs';
import {driveStructureCoordinates} from './structure-coordinates.mjs';
import {createHangerDraft,createHangerFrameReference,createRoofReference,hangerFrameDefinition} from './hanger-draft.mjs';
import {createHangerInstances,hangerTemplateReference,hangerTemplatePath} from './hanger-installation.mjs';
import {requiresHanger} from './hanger-planning.mjs';
import {swMatrix,transformPoint} from './geometry.mjs';
import {nativeSceneToPreview} from './structure-installation-preview.mjs';
import {removeOriginalJointHangers} from './original-joint-design.mjs';
import {memoizeDefinition} from './definition-memo.mjs';
export const originalAutomaticHangerVersion='original-joint-hangers/1';
const one=(rows,label)=>{if(rows.length!==1)throw new DefinitionError(label+'未唯一对应');return rows[0];};

export async function installOriginalJointHangers(input,read,{memo,roofPlanes}={}){
  const project=validateProject(input),layout=project.originalDesign?.result.hangers;
  if(!project.originalDesign||layout===undefined&&project.originalDesign.status!=='ready')return project;
  removeOriginalJointHangers(project);
  if(layout?.length===0)return project;
  const design=(await buildDesignExport(project,{readTemplate:read,memo})).design,cabinet=one(project.objects.filter(row=>row.cabinetBinding?.scenario===project.scenario),'柜体场景'),planes=roofPlanes??await memoizeDefinition(memo,read,'cabinet-roof',[design.cabinet.templatePath,design.cabinet.configuration,project.scenario,design.inputs],async()=>{const source=await read(design.cabinet.templatePath),derived=await deriveCabinetScene(source,design.cabinet.configuration,project.scenario,design.inputs,read);return collectOriginalRoofPlanes(derived,design.cabinet.configuration);}),roof=createRoofReference(cabinet,planes),frames=[],framesById=new Map();
  for(const plan of project.frameDesign.plans){
    const original=project.originalDesign.result.objects.find(row=>row.id===project.frameDesign.inputs[plan.positionIndex-1].originalObjectId);if(!requiresHanger(original.frameTemplateKey)||layout&&!layout.some(row=>row.frameId===original.id))continue;
    const object=one(project.objects.filter(row=>row.frameBinding?.positionKey===plan.positionKey),'基架实例'),model=one(design.frames.models.filter(row=>row.sourceName===plan.sourceName),'基架定义'),installed=one(design.installation.preview.instances.filter(row=>row.instanceId==='frame:'+plan.positionKey),'基架安装位置');object.transform=structuredClone(installed.transform);
    const template=await read(model.templatePath),configuration=template.configurations.find(row=>row.name===model.configuration),updated=structuredClone(configuration),graph=one(model.structure.models.filter(row=>row.templatePath===model.templatePath&&row.configuration===model.configuration),'基架驱动文档');
    if(!driveStructureCoordinates(configuration,updated,graph))throw new DefinitionError('吊杆缺少受驱基架安装坐标');
    const coordinate=one(updated.features.filter(row=>row.type==='CoordSys'&&row.name==='吊杆配合坐标系'&&!row.suppressed),'吊杆根坐标'),reference={key:JSON.stringify([[],coordinate.id,coordinate.name]),featureId:coordinate.id,featureName:coordinate.name,ownerPath:model.templatePath,configuration:model.configuration,componentChain:[],localTransform:swMatrix(coordinate.definition.properties.Transform.transform),source:coordinate.offlineCoordinateSolution?.source??'ICoordinateSystemFeatureData.Transform',units:'mm'};
    const frame=createHangerFrameReference(object,reference,hangerFrameDefinition(model.templatePath,[{name:'基架宽度',value:model.width}]));
    frames.push(frame);framesById.set(original.id,frame);
  }
  if(layout!==undefined){
    const ids=new Set(),template=await read(hangerTemplatePath),reference=hangerTemplateReference(template,'默认');
    for(const placement of layout){
      const frame=framesById.get(placement.frameId);
      if(!frame||typeof placement.id!=='string'||!placement.id||ids.has(placement.id)||!Number.isFinite(placement.offset))throw new DefinitionError('预览吊架缺少有效身份或基架归属');
      ids.add(placement.id);
      const draft={...createHangerDraft({name:'吊架',frames:[{...frame,localOffset:placement.offset}],roof},project.objects),id:project.id+':hanger:'+placement.id,automatic:originalAutomaticHangerVersion};
      const plan=draft.plans[0],expected=transformPoint(placement.center,nativeSceneToPreview);
      if(Math.hypot(...plan.pose.origin.map((value,axis)=>value-expected[axis]))>1.0||Math.abs(plan.installationHeight-placement.installationHeight)>1.0)
        throw new DefinitionError('吊架原生模板安装位置与草稿不一致：'+placement.id);
      project.hangerPlans.push(draft);project.objects.push({...createHangerInstances(draft,reference)[0],id:draft.id+':model',originalDesignBinding:{version:'original-joint-object/1',objectId:placement.id,rowId:placement.id}});
    }
    return validateProject(project);
  }
  if(!frames.length)return project;
  const draft={...createHangerDraft({name:'吊杆',frames,roof},project.objects),id:project.id+':original-hangers',automatic:originalAutomaticHangerVersion},template=await read(hangerTemplatePath),reference=hangerTemplateReference(template,'默认');
  project.hangerPlans.push(draft);project.objects.push(...createHangerInstances(draft,reference).map(object=>({...object,id:object.parentId+':original-hanger'})));project.originalDesign.automaticHangers={version:originalAutomaticHangerVersion,status:'ready',frameIds:frames.map(row=>row.objectId)};
  return validateProject(project);
}
