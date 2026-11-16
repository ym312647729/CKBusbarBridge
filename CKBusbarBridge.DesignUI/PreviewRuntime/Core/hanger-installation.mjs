import {DefinitionError} from './expressions.mjs';
import {hangerRules} from './hanger-planning.mjs';
import {swMatrix,inverseRigidMatrix,multiplyMatrix} from './geometry.mjs';
import {validateInstallationMatrix} from './installation-pose.mjs';
export const hangerTemplatePath='模板/吊杆模板/5GX.000.001.SLDASM';

export function hangerTemplateReference(template,configurationName){
  if(template.source.path!==hangerTemplatePath)throw new DefinitionError('吊杆安装未选择原始业务模板');
  const configuration=template.configurations.find(row=>row.name===configurationName),coordinates=configuration?.features.filter(row=>row.type==='CoordSys'&&row.name===hangerRules.coordinateName&&!row.suppressed);
  if(coordinates?.length!==1)throw new DefinitionError('吊杆模板没有唯一原始根安装坐标系');
  const coordinate=coordinates[0],localTransform=swMatrix(coordinate.definition?.properties.Transform?.transform);validateInstallationMatrix(localTransform);
  return {templatePath:hangerTemplatePath,configuration:configurationName,sourceSha256:template.source.sha256,featureId:coordinate.id,featureName:coordinate.name,localTransform};
}
function poseMatrix(pose){return [...pose.along,0,...pose.up,0,...pose.across,0,...pose.origin,1];}
export function createHangerInstances(draft,reference,existing=[]){
  if(draft?.kind!=='hanger-installation-draft'||reference?.templatePath!==hangerTemplatePath||reference.featureName!==hangerRules.coordinateName||typeof reference.sourceSha256!=='string'||!reference.sourceSha256)throw new DefinitionError('吊杆实例缺少原始安装规划或模板来源');
  validateInstallationMatrix(reference.localTransform);
  return draft.plans.map((plan,index)=>{
    const previous=existing.filter(object=>object.hangerBinding?.draftId===draft.id&&object.hangerBinding.frameId===plan.pose.frameId);if(previous.length>1)throw new DefinitionError('同一基架存在多个已安装吊杆');
    return {id:previous[0]?.id??crypto.randomUUID(),name:draft.name+' · '+(index+1),category:'hanger',parentId:plan.pose.frameId,templatePath:reference.templatePath,configuration:reference.configuration,sourceSha256:reference.sourceSha256,parameters:{},suppression:{},transform:multiplyMatrix(poseMatrix(plan.pose),inverseRigidMatrix(reference.localTransform)),visible:previous[0]?.visible??true,hangerDimensions:{width:plan.pose.width,angleLength:plan.angleLength},hangerBinding:{draftId:draft.id,frameId:plan.pose.frameId,templateReference:structuredClone(reference)}};
  });
}
export function resolveHangerInstances(objects,drafts){
  const used=new Set();return objects.map(object=>{
    if(!object.hangerBinding){if(object.hangerDimensions)throw new DefinitionError('吊杆驱动尺寸缺少安装规划归属');return object;}
    const binding=object.hangerBinding,draft=drafts.find(row=>row.id===binding.draftId),plan=draft?.plans.find(row=>row.pose.frameId===binding.frameId),reference=binding.templateReference;
    if(!plan||object.category!=='hanger'||object.parentId!==binding.frameId||object.templatePath!==reference?.templatePath||object.configuration!==reference.configuration||object.sourceSha256!==reference.sourceSha256||Object.keys(object.parameters??{}).length||Object.keys(object.suppression??{}).length)throw new DefinitionError('已安装吊杆的基架归属、模板配置或驱动状态不符');
    const key=plan&&draft.automatic&&draft.input.frames.some(frame=>frame.localOffset!==undefined)?draft.id:binding.frameId;
    if(used.has(key))throw new DefinitionError('同一位置不能重复安装受控吊杆');used.add(key);
    return {...object,...createHangerInstances({...draft,plans:[plan]},reference,[object])[0],id:object.id,name:object.name,visible:object.visible};
  });
}
