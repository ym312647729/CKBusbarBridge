import {DefinitionError} from './expressions.mjs';
import {swMatrix,multiplyMatrix,transformPoint} from './geometry.mjs';
import {validateInstallationMatrix} from './installation-pose.mjs';

function featureActive(feature,configuration){
  const visited=new Set();
  while(feature){
    if(feature.suppressed)return false;
    if(visited.has(feature.name))throw new DefinitionError('坐标系所属特征层级循环');
    visited.add(feature.name);feature=feature.parent?configuration.features.find(parent=>parent.name===feature.parent):null;
  }
  return true;
}

// CaptureComponents uses GetComponents(true): every child transform is relative to its own definition.
export async function collectTemplateInterfaces(template,configurationName,loadTemplate,overrides={}){
  const interfaces=[],diagnostics=[];
  if(Object.keys(overrides).length)return {interfaces,diagnostics:[{path:template.source.path,message:'尺寸修改后的原生坐标系尚未求解，不提供旧接口坐标'}]};
  const identity=[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1];
  async function visit(definition,name,placement,componentChain,ancestry){
    const identityKey=definition.source.path+'\0'+name;
    if(ancestry.includes(identityKey))throw new DefinitionError('接口模板引用形成循环：'+definition.source.path);
    const configuration=definition.configurations.find(row=>row.name===name);
    if(!configuration)throw new DefinitionError('接口源配置不存在：'+name);
    for(const feature of configuration.features.filter(row=>row.type==='CoordSys'&&featureActive(row,configuration))){
      try{
        const nativeTransform=feature.definition?.properties.Transform?.transform;
        const localTransform=multiplyMatrix(placement,swMatrix(nativeTransform));validateInstallationMatrix(localTransform);
        const key=JSON.stringify([componentChain.map(component=>[component.id,component.name]),feature.id,feature.name]);
        if(interfaces.some(row=>row.key===key))throw new DefinitionError('原坐标系身份重复，无法唯一对应');
        interfaces.push({key,featureId:feature.id,featureName:feature.name,ownerPath:definition.source.path,configuration:name,componentChain:structuredClone(componentChain),localTransform,source:feature.offlineCoordinateSolution?.source??'ICoordinateSystemFeatureData.Transform',units:'mm'});
      }catch(error){diagnostics.push({path:definition.source.path,configuration:name,feature:feature.name,componentChain:structuredClone(componentChain),message:error.message});}
    }
    for(const component of configuration.components??[]){
      if(component.suppression===0||component.representation?.mode==='deferred')continue;
      const child=component.isVirtual?{...component.embeddedDefinition,source:{...component.embeddedDefinition?.source,path:definition.source.path+'#embedded/'+component.id}}:await loadTemplate(component.path);
      if(!child?.configurations)throw new DefinitionError('接口组件定义缺失：'+component.name);
      await visit(child,component.configuration,multiplyMatrix(placement,swMatrix(component.transform)),[...componentChain,{id:component.id,name:component.name,path:component.path,configuration:component.configuration}], [...ancestry,identityKey]);
    }
  }
  await visit(template,configurationName,identity,[],[]);
  return {interfaces,diagnostics};
}

export function createEndpointReference(object,coordinate){
  if(Object.keys(object.parameters??{}).length)throw new DefinitionError('尺寸修改后的坐标系尚未求解，不能绑定旧接口');
  validateInstallationMatrix(coordinate.localTransform);
  return {objectId:object.id,key:coordinate.key,templatePath:object.templatePath,configuration:object.configuration,sourceSha256:object.sourceSha256,featureName:coordinate.featureName,ownerPath:coordinate.ownerPath,componentChain:structuredClone(coordinate.componentChain),localTransform:[...coordinate.localTransform]};
}

export function resolveRouteEndpoints(input,objects){
  const resolved=structuredClone(input);
  for(const side of ['start','end']){
    const reference=input.endpointReferences?.[side];if(!reference)continue;
    const object=objects.find(row=>row.id===reference.objectId);
    if(!object)throw new DefinitionError('路径绑定的接口对象已删除，请先改为手动坐标或删除路径');
    if(reference.templatePath!==object.templatePath||reference.configuration!==object.configuration||reference.sourceSha256!==object.sourceSha256)throw new DefinitionError('路径接口源配置已变化，请先解除接口绑定后重新选择');
    if(Object.keys(object.parameters??{}).length)throw new DefinitionError('绑定接口的尺寸尚未求解，请先解除路径接口绑定');
    validateInstallationMatrix(reference.localTransform);
    resolved[side]=transformPoint(reference.localTransform.slice(12,15),object.transform);
  }
  return resolved;
}

export function endpointReferenceIssue(reference,geometryResult){
  if(!geometryResult?.ok)return '接口所属对象尚未构造';
  const actual=geometryResult.interfaces?.find(row=>row.key===reference.key);
  if(!actual)return '绑定的原坐标系未在当前定义中读取到，请重新选择';
  if(actual.localTransform.some((value,index)=>Math.abs(value-reference.localTransform[index])>1e-7))return '保存的接口坐标与当前原定义不符，请重新选择';
  return null;
}
