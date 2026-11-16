import {DefinitionError} from './expressions.mjs';
import {validateInstallationMatrix} from './installation-pose.mjs';
import {multiplyMatrix} from './feature-patterns.mjs';

export const hangerRules=Object.freeze({version:'migrated-hanger/1',coordinateName:'吊杆配合坐标系',fixedHeight:10+1000-80-88,modelTolerance:1e-6});
const dot=(left,right)=>left.reduce((sum,value,index)=>sum+value*right[index],0);
const difference=(left,right)=>left.map((value,index)=>value-right[index]);
const vector=(value,label)=>{if(!Array.isArray(value)||value.length!==3||!value.every(Number.isFinite))throw new DefinitionError(label+'必须提供有限三维坐标');return [...value];};
const normalized=(value,label)=>{const result=vector(value,label),length=Math.hypot(...result);if(length<1e-9)throw new DefinitionError(label+'不能为零向量');return result.map(coordinate=>coordinate/length);};
const positive=(value,label)=>{if(!Number.isFinite(value)||value<=0)throw new DefinitionError(label+'必须为有限正数');return value;};
const ordinal=(left,right)=>left<right?-1:left>right?1:0;

export function requiresHanger(templateKey){return templateKey!=='LargeOffsetCorner';}

// Used only where the migrated workflow explicitly identifies this verified template key.
// Source coordinates take priority for an existing object; this is not an identity inference.
export function hangerTemplateCoordinate(templateKey,width,height){
  positive(width,'基架宽度');positive(height,'基架高度');
  if(templateKey==='SideToSidePhaseChange029')return [1,0,0,0,0,0,-1,0,0,1,0,0,0,width/2,height/2,1];
  if(!['Regular','SmallOffset','FaceToWallLarge1','FaceToWallLarge2'].includes(templateKey))throw new DefinitionError('未核验的基架吊杆安装坐标系模板：'+templateKey);
  return [1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1];
}

export function hangerPose(frameId,width,coordinateTransform,objectTransform){
  if(typeof frameId!=='string'||!frameId.trim())throw new DefinitionError('吊杆缺少唯一基架实例');
  positive(width,'基架宽度');validateInstallationMatrix(coordinateTransform);
  let matrix=[...coordinateTransform];if(objectTransform){validateInstallationMatrix(objectTransform);matrix=multiplyMatrix(objectTransform,matrix);}
  return {frameId,width,origin:matrix.slice(12,15),along:matrix.slice(0,3),up:matrix.slice(4,7),across:matrix.slice(8,11)};
}

function validatedPose(input){
  const origin=vector(input?.origin,'基架原点'),along=normalized(input?.along,'吊杆纵向'),up=normalized(input?.up,'吊杆安装方向'),across=normalized(input?.across,'吊杆横向');
  if(typeof input?.frameId!=='string'||!input.frameId.trim()||!Number.isFinite(input.width)||input.width<=0||Math.abs(dot(along,up))>1e-6||Math.abs(dot(along,across))>1e-6||Math.abs(dot(up,across))>1e-6)throw new DefinitionError('吊杆缺少有效的基架实例、宽度或正交安装坐标系');
  return {frameId:input.frameId,width:input.width,origin,along,up,across};
}

export function selectRoofComponentName(componentNames){
  if(!Array.isArray(componentNames)||!componentNames.every(name=>typeof name==='string'))throw new DefinitionError('屋面组件名称清单无效');
  let label='横梁',matches=componentNames.filter(name=>/^横梁(?:-[0-9]+)?$/.test(name.split('/').at(-1)));
  if(!matches.length){label='屋顶';matches=componentNames.filter(name=>/^屋顶(?:-[0-9]+)?$/.test(name.split('/').at(-1)));}
  if(matches.length!==1)throw new DefinitionError(matches.length?'总装必须唯一包含“'+label+'”组件，当前匹配数量：'+matches.length:'总装未找到名为“横梁”或“屋顶”的有效屋面组件');
  return matches[0];
}

export function planHanger(inputPose,roof){
  const pose=validatedPose(inputPose);
  if(typeof roof?.identity!=='string'||!roof.identity.trim()||!Array.isArray(roof.faces)||!roof.faces.length)throw new DefinitionError('屋面缺少唯一身份或实际平面');
  const matches=[];
  for(const [faceIndex,face]of roof.faces.entries()){
    const point=vector(face.point,'屋面平面原点'),normal=normalized(face.normal,'屋面外法向');
    if(normal.some((value,index)=>Math.abs(value+pose.up[index])>1e-6))continue;
    const installationHeight=dot(difference(point,pose.origin),normal)/dot(normal,pose.up);matches.push({installationHeight,faceIndex});
  }
  if(!matches.length)throw new DefinitionError('屋面缺少与吊杆安装方向垂直且朝下的实际平面');
  // The existing workflow uses the lowest downward plane, extended infinitely.
  // Do not replace it by a higher plane when the lowest one lacks clearance.
  const selected=matches.reduce((lowest,current)=>current.installationHeight<lowest.installationHeight?current:lowest);
  if(!Number.isFinite(selected.installationHeight)||selected.installationHeight<=hangerRules.fixedHeight)throw new DefinitionError('基架“'+pose.frameId+'”到屋面的空间不足，反算角钢长度必须大于零');
  return {pose,installationHeight:selected.installationHeight,angleLength:selected.installationHeight-hangerRules.fixedHeight,roofIdentity:roof.identity,roofFaceIndex:selected.faceIndex,ruleVersion:hangerRules.version};
}

export function planHangers(frames,roof){
  if(!Array.isArray(frames))throw new DefinitionError('吊杆基架清单无效');
  const identities=new Set(),plans=[];
  for(const frame of frames){if(!frame.frameId||identities.has(frame.frameId))throw new DefinitionError('多个吊杆计划指向同一基架实例');identities.add(frame.frameId);if(!requiresHanger(frame.templateKey))continue;plans.push(planHanger(hangerPose(frame.frameId,frame.width,frame.coordinateTransform,frame.objectTransform),roof));}
  return plans;
}

export function groupHangerModels(plans,existing=[]){
  if(!Array.isArray(plans)||!Array.isArray(existing))throw new DefinitionError('吊杆模型分组输入必须为清单');
  for(const model of existing){if(typeof model?.path!=='string'||!model.path.trim()||typeof model.configuration!=='string'||!model.configuration||typeof model.frameId!=='string'||!model.frameId)throw new DefinitionError('已有吊杆模型缺少路径、配置或基架归属');positive(model.width,'已有吊杆宽度');positive(model.angleLength,'已有吊杆角钢长度');}
  const tolerance=hangerRules.modelTolerance,same=(widthA,lengthA,widthB,lengthB)=>Math.abs(widthA-widthB)<=tolerance&&Math.abs(lengthA-lengthB)<=tolerance;
  const groups=[];
  for(const plan of plans){positive(plan?.pose?.width,'吊杆模型宽度');positive(plan?.angleLength,'吊杆角钢长度');let group=groups.find(group=>same(group.plans[0].pose.width,group.plans[0].angleLength,plan.pose.width,plan.angleLength));if(!group){group={plans:[]};groups.push(group);}group.plans.push(plan);}
  const models=[...existing].sort((left,right)=>ordinal(left.path.toUpperCase(),right.path.toUpperCase())||ordinal(left.configuration,right.configuration)||ordinal(left.frameId,right.frameId)),usedPaths=new Set();
  return groups.map(group=>{const model=models.find(model=>!usedPaths.has(model.path.toUpperCase())&&group.plans.every(plan=>same(model.width,model.angleLength,plan.pose.width,plan.angleLength)));if(model)usedPaths.add(model.path.toUpperCase());return {plans:structuredClone(group.plans),existingModel:model?structuredClone(model):null};});
}
