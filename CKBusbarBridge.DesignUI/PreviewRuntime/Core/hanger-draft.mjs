import {DefinitionError} from './expressions.mjs';
import {hangerRules,hangerPose,planHanger,groupHangerModels} from './hanger-planning.mjs';
import {createEndpointReference} from './template-interfaces.mjs';
import {transformPoint} from './geometry.mjs';
import {transformDirection} from './template-planes.mjs';
import {validateInstallationMatrix} from './installation-pose.mjs';

const verifiedFrames=new Map([
  ['模板/基架模板/常规基架/5GX.355.024.SLDASM','Regular'],
  ['模板/基架模板/小偏移基架/5GX.355.032.SLDASM','SmallOffset'],
  ['模板/基架模板/大基架1/5GX.355.026.SLDASM','FaceToWallLarge1'],
  ['模板/基架模板/大基架2/5GX.355.027.SLDASM','FaceToWallLarge2'],
  ['模板/基架模板/侧对侧换相基架/5GX.355.029.SLDASM','SideToSidePhaseChange029']
]);
const shapeSignature=object=>JSON.stringify([Object.entries(object.parameters??{}).sort(),Object.entries(object.suppression??{}).sort()]);
const same=(left,right)=>JSON.stringify(left)===JSON.stringify(right);
function sourceObject(reference,objects){
  const object=objects.find(row=>row.id===reference?.objectId);
  if(!object)throw new DefinitionError('吊杆规划绑定的对象已删除，请先删除对应规划');
  if(Object.keys(object.suppression??{}).length)throw new DefinitionError('吊杆绑定对象的压缩修改尚未求解');
  if(object.templatePath!==reference.templatePath||object.configuration!==reference.configuration||object.sourceSha256!==reference.sourceSha256||shapeSignature(object)!==reference.shapeSignature)throw new DefinitionError('吊杆规划绑定的模板、配置或尺寸已变化，请先删除对应规划再修改');
  validateInstallationMatrix(object.transform);return object;
}
export function hangerFrameDefinition(templatePath,parameters){
  const templateKey=verifiedFrames.get(templatePath),width=parameters.find(row=>row.name==='基架宽度')?.value;
  return templateKey&&Number.isFinite(width)&&width>0?{templateKey,width}:null;
}
export function createHangerFrameReference(object,coordinate,definition){
  if(Object.keys(object.suppression??{}).length)throw new DefinitionError('基架压缩修改后的安装接口尚未求解');
  if(object.category!=='frame'||verifiedFrames.get(object.templatePath)!==definition?.templateKey||coordinate?.featureName!==hangerRules.coordinateName||coordinate.componentChain?.length!==0||!Number.isFinite(definition.width)||definition.width<=0)throw new DefinitionError('请选择已核验基架的根吊杆配合坐标系及原始宽度');
  return {...createEndpointReference(object,coordinate),shapeSignature:shapeSignature(object),width:definition.width,templateKey:definition.templateKey};
}
export function createRoofReference(object,metadata){
  if(Object.keys(object.suppression??{}).length)throw new DefinitionError('屋面压缩修改后的平面尚未求解');
  if(!metadata?.complete||!metadata.planes?.length)throw new DefinitionError('屋面完整解析平面尚不可用');
  return {objectId:object.id,templatePath:object.templatePath,configuration:object.configuration,sourceSha256:object.sourceSha256,shapeSignature:shapeSignature(object),planes:structuredClone(metadata.planes)};
}
export function createHangerDraft(input,objects){
  if(!input||!Array.isArray(input.frames)||!input.frames.length)throw new DefinitionError('请至少选择一个具有原始吊杆安装坐标系的基架');
  const roofObject=sourceObject(input.roof,objects),planeKeys=new Set();
  if(!Array.isArray(input.roof.planes)||!input.roof.planes.length)throw new DefinitionError('吊杆屋面缺少完整实际平面');
  const faces=input.roof.planes.map(plane=>{
    if(typeof plane.key!=='string'||!plane.key||planeKeys.has(plane.key)||plane.source!=='analytic-source-extrusion'||![plane.point,plane.normal].every(vector=>Array.isArray(vector)&&vector.length===3&&vector.every(Number.isFinite)))throw new DefinitionError('吊杆屋面平面来源、身份或坐标无效');
    planeKeys.add(plane.key);return {key:plane.key,point:transformPoint(plane.point,roofObject.transform),normal:transformDirection(plane.normal,roofObject.transform)};
  }),roof={identity:roofObject.id,faces},frameIds=new Set(),plans=[];
  for(const reference of input.frames){
    const object=sourceObject(reference,objects);
    if(frameIds.has(object.id)||object.id===roofObject.id)throw new DefinitionError('吊杆规划重复绑定基架或将基架作为屋面');frameIds.add(object.id);
    if(object.category!=='frame'||verifiedFrames.get(object.templatePath)!==reference.templateKey||reference.featureName!==hangerRules.coordinateName||reference.componentChain?.length!==0||Object.keys(object.parameters??{}).length)throw new DefinitionError('吊杆基架根安装接口尚未核验');
    const pose=hangerPose(object.id,reference.width,reference.localTransform,object.transform);
    if(reference.localOffset!==undefined){
      if(!Number.isFinite(reference.localOffset))throw new DefinitionError('吊架沿架安装偏移无效');
      pose.origin=pose.origin.map((value,axis)=>value+pose.along[axis]*reference.localOffset);
    }
    const plan=planHanger(pose,roof);plans.push({...plan,roofPlaneKey:faces[plan.roofFaceIndex].key});
  }
  return {id:crypto.randomUUID(),name:input.name||'吊杆安装规划',kind:'hanger-installation-draft',status:'planning',ruleVersion:hangerRules.version,input:structuredClone(input),plans,groups:groupHangerModels(plans).map(group=>({frameIds:group.plans.map(plan=>plan.pose.frameId),width:group.plans[0].pose.width,angleLength:group.plans[0].angleLength})),pending:['真实吊杆模板宽度与角钢长度驱动','吊杆实例配合安装','吊杆及周边净距校验']};
}
export function validateHangerDraft(draft,objects){if(draft?.kind!=='hanger-installation-draft'||typeof draft.id!=='string'||!draft.id)throw new DefinitionError('吊杆规划类型或标识无效');if(draft.automatic!==undefined&&draft.automatic!=='original-joint-hangers/1')throw new DefinitionError('自动吊杆版本无效');return {...createHangerDraft(draft.input,objects),id:draft.id,name:draft.name||draft.input.name||'吊杆安装规划',...draft.automatic?{automatic:draft.automatic}:{}};}
export function hangerReferenceIssues(draft,results){
  const issues=[],roofResult=results.get(draft.input.roof.objectId);
  if(!roofResult?.roofPlanes?.complete||!same(roofResult.roofPlanes.planes,draft.input.roof.planes))issues.push('保存的屋面平面与当前定义不符或尚未读取，请重新选择');
  for(const reference of draft.input.frames){
    const result=results.get(reference.objectId),coordinate=result?.interfaces?.find(row=>row.key===reference.key);
    if(!coordinate||!same(coordinate.localTransform,reference.localTransform)||result.hangerFrame?.width!==reference.width||result.hangerFrame?.templateKey!==reference.templateKey)issues.push('基架原始吊杆接口或宽度与当前定义不符或尚未读取，请重新选择');
  }
  return issues;
}
