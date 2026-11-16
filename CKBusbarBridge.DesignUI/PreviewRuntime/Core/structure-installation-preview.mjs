import {DefinitionError} from './expressions.mjs';
import {deriveCabinetScene} from './cabinet-scene.mjs';
import {driveStructureCoordinates} from './structure-coordinates.mjs';
import {swMatrix,multiplyMatrix} from './geometry.mjs';
import {coordinateInstallation,originalFrameInstallation} from './structure-preview-placement.mjs';
import {memoizeDefinition} from './definition-memo.mjs';
export const nativeSceneToPreview=[1,0,0,0,0,0,1,0,0,-1,0,0,0,0,0,1];

// No solids, meshes or native model execution. Resolve the JSON installation
// graph from the same driven mounting datums used by the independent preview.
export async function evaluateStructureInstallation(installation,{project,cabinet,bases,frames,memo,memoOwner=read},read){
  const models=new Map([...bases.models.map(model=>[model.modelId,model]),...(frames?.models??[]).map(model=>['frame-model:'+model.sourceName,model])]);
  for(const model of bases.models)for(const placement of model.insulators)models.set(placement.design.modelId,placement.design);
  for(const placement of installation.frameInsulators)models.set(placement.design.modelId,placement.design);
  const cabinetConfiguration=await memoizeDefinition(memo,memoOwner,'cabinet-installation',[cabinet.templatePath,cabinet.configuration,project.scenario,bases.record],async()=>{const cabinetSource=await read(cabinet.templatePath),derivedCabinet=await deriveCabinetScene(cabinetSource,cabinet.configuration,project.scenario,bases.record,read);return derivedCabinet.template.configurations.find(row=>row.name===cabinet.configuration);}),rootCoordinates=new Map([['cabinet-scene',cabinetConfiguration]]);
  for(const [modelId,model]of models){
    if(['universal-insulator-design','mounted-insulator-design'].includes(model.kind))continue;
    const template=await read(model.templatePath),source=template.configurations.find(row=>row.name===model.configuration),updated=structuredClone(source),graphModel=model.structure.models.find(row=>row.templatePath===model.templatePath&&row.configuration===model.configuration);
    if(!graphModel||!driveStructureCoordinates(source,updated,graphModel))throw new DefinitionError('结构JSON缺少受驱安装坐标：'+modelId);rootCoordinates.set(modelId,updated);
  }
  function coordinate(endpoint){
    const instance=installation.instances.find(row=>row.instanceId===endpoint.instanceId),model=models.get(instance.modelId);
    if(['universal-insulator-design','mounted-insulator-design'].includes(model?.kind)){
      const datum=model.installation;if(endpoint.coordinateName!==datum.coordinateName)throw new DefinitionError('绝缘子JSON安装坐标名不符');
      return [datum.xFlipped?-1:1,0,0,0,0,1,0,0,0,0,datum.zFlipped?-1:1,0,...datum.originMm,1];
    }
    const configuration=rootCoordinates.get(instance.modelId),matches=configuration?.features.filter(row=>row.type==='CoordSys'&&row.name===endpoint.coordinateName&&row.id===endpoint.featureId)??[];
    if(matches.length!==1)throw new DefinitionError('结构JSON安装原点未唯一求值：'+endpoint.instanceId+'/'+endpoint.coordinateName);return swMatrix(matches[0].definition.properties.Transform.transform);
  }
  const rootObject=project.objects.find(object=>object.cabinetBinding?.scenario===project.scenario),transforms=new Map([['cabinet-scene',rootObject?.transform??nativeSceneToPreview]]),connections=[],diagnostics=[];
  for(const connection of installation.connections){
    const parentPose=transforms.get(connection.parent.instanceId);if(!parentPose){diagnostics.push({instanceId:connection.child.instanceId,message:'缺少上游安装位置：'+connection.parent.instanceId});continue;}
    if(transforms.has(connection.child.instanceId))throw new DefinitionError('结构安装实例有多个位置来源');
    const parent=coordinate(connection.parent),child=coordinate(connection.child),instance=installation.instances.find(row=>row.instanceId===connection.child.instanceId),position=frames?.plans.find(row=>row.positionKey===instance.positionKey),originalInput=position?frames.inputs[position.positionIndex-1]:null;
    let transform=coordinateInstallation(parentPose,parent,child);
    if(originalInput?.originalPreviewPorts){const outlet=coordinate({...connection.child,coordinateName:position.outletCoordinateName,featureId:rootCoordinates.get(instance.modelId).features.find(row=>row.type==='CoordSys'&&row.name===position.outletCoordinateName)?.id});transform=originalFrameInstallation(originalInput,child,outlet);}
    const target=multiplyMatrix(parentPose,parent),actual=multiplyMatrix(transform,child),errorMm=Math.hypot(...actual.slice(12,15).map((value,axis)=>value-target[12+axis]));
    transforms.set(connection.child.instanceId,transform);connections.push({parentInstanceId:connection.parent.instanceId,childInstanceId:connection.child.instanceId,kind:connection.kind,originClosureMm:connection.referenceOnly?null:errorMm,...connection.referenceOnly?{referenceOriginOffsetMm:errorMm}: {}});
  }
  return {version:'original-structure-preview-poses/1',units:'column-major-mm',schematic:true,instances:[...transforms].map(([instanceId,transform])=>({instanceId,transform})),connections,diagnostics,unplacedInstances:installation.instances.filter(instance=>!transforms.has(instance.instanceId)).map(instance=>instance.instanceId),nativeModelValidationRequired:false};
}
