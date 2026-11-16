import {DefinitionError} from './expressions.mjs';
import {planCabinetBases} from './cabinet-base-planning.mjs';
import {multiplyMatrix,inverseRigidMatrix,transformPoint} from './geometry.mjs';
import {geometrySignature} from './project.mjs';
import {validateInstallationMatrix} from './installation-pose.mjs';
import {structureSupportCoordinate} from './structure-support-binding.mjs';

export function baseRootCoordinateName(scenario,record,position){
  if(scenario!=='side-to-side')return position.rootCoordinateName;
  return position.positionName[0]+'柜-'+(position.positionName.endsWith('前')?'前':'后')+'（'+(record.values['General.CabinetTopOpeningMode']==='LeftRight'?'左右开孔':'前后开孔')+'）';
}
export function coordinateInstallation(parentPose,parentCoordinate,childCoordinate){
  const placement=multiplyMatrix(multiplyMatrix(parentPose,parentCoordinate),inverseRigidMatrix(childCoordinate));validateInstallationMatrix(placement);return placement;
}

// Original planner ports describe every physical frame independently. Their
// list order can put both straight banks before the two offset corners; it is
// not an assembly traversal. Preserve the native model's vertical axis and
// fit its driven inlet/outlet to that original pair, without stretching it.
export function originalFrameInstallation(input,inlet,outlet){
  if(!input?.originalPreviewPorts)return null;
  const [targetStart,targetEnd]=input.originalPreviewPorts,sourceStart=inlet.slice(12,15),sourceEnd=outlet.slice(12,15),localDelta=sourceEnd.map((value,index)=>value-sourceStart[index]),targetDelta=targetEnd.map((value,index)=>value-targetStart[index]);
  const angle=Math.atan2(targetDelta[1],targetDelta[0])-Math.atan2(-localDelta[2],localDelta[0]),cosine=Math.cos(angle),sine=Math.sin(angle),sideMounted=input.templateIdentity==='SideToSidePhaseChange029';
  // 029's section is mounted on its side: native -Z is assembly up, and its
  // dedicated hanger datum is [0,W/2,H/2]. Its ports alone leave this roll
  // unconstrained; using 024's native Y-up pose rotates the hanger sideways.
  const transform=sideMounted?[cosine,sine,0,0,sine,-cosine,0,0,0,0,-1,0,0,0,0,1]:[cosine,sine,0,0,0,0,1,0,sine,-cosine,0,0,0,0,0,1];
  const rotated=transformPoint(sourceStart,transform);for(let axis=0;axis<3;axis++)transform[12+axis]=targetStart[axis]-rotated[axis];
  const actualEnd=transformPoint(sourceEnd,transform),error=Math.hypot(...actualEnd.map((value,axis)=>value-targetEnd[axis]));
  if(error>1.0)throw new DefinitionError('基架受驱端点与原规划尺寸不一致：'+input.sourceName+'，误差 '+error.toFixed(1)+' mm');
  if(input.templateIdentity==='FaceToFaceLargeOffsetCorner028'){
    const expectedCenter=input.originalCornerCenter;
    if(expectedCenter?.length!==3||!expectedCenter.every(Number.isFinite))throw new DefinitionError('大偏移拐角缺少原安装中心：'+input.sourceName);
    const centerError=Math.hypot(...expectedCenter.map((value,axis)=>value-transform[12+axis]));
    if(centerError>1.0)throw new DefinitionError('大偏移拐角安装中心与原规划不一致：'+input.sourceName+'，误差 '+centerError.toFixed(1)+' mm');
  }
  validateInstallationMatrix(transform);return transform;
}

export function orderedFrameInstallationPlans(frames){
  if(!frames?.inputs?.length||frames.inputs.some(input=>!input.originalPreviewPorts))return frames?.plans??[];
  const ports=plan=>frames.inputs[plan.positionIndex-1].originalPreviewPorts,near=(a,b)=>Math.hypot(...a.map((value,index)=>value-b[index]))<.01;
  const remaining=[...frames.plans],ordered=[];
  while(remaining.length){
    const matches=ordered.length?remaining.filter(plan=>near(ports(ordered.at(-1))[1],ports(plan)[0])):[];
    if(matches.length>1)throw new DefinitionError('原规划基架端口存在多个下一节');
    const next=matches[0]??remaining.find(plan=>!remaining.some(other=>other!==plan&&near(ports(other)[1],ports(plan)[0])));
    if(!next)throw new DefinitionError('原规划基架端口存在循环');ordered.push(next);remaining.splice(remaining.indexOf(next),1);
  }
  return ordered;
}

// Local geometry/coordinates come from the current Worker revision. Placement
// is derived state, so this does not create an extra user undo transaction.
export function structurePreviewPlacements(project,results){
  const assignments=[],diagnostics=[],placements=new Map();
  const objectBy=(test,label)=>{const found=project.objects.filter(test);if(found.length>1)throw new DefinitionError('结构安装对象重复：'+label);return found[0];};
  const coordinate=(object,name)=>{
    if(!object)return null;const result=results.get(object.id);
    if(!result?.ok||result.signature&&!result.signature.startsWith(geometrySignature(object)))return null;
    if(object.structureSupportBinding&&name==='安装坐标系')return structureSupportCoordinate(object.insulatorPlanInput);
    const matches=result.interfaces?.filter(row=>row.featureName===name&&row.componentChain.length===0)??[];
    if(matches.length>1)throw new DefinitionError('结构根坐标不唯一：'+name);return matches[0]?.localTransform??null;
  };
  const connect=(parent,parentName,child,childName)=>{
    if(!child)return false;
    const parentPose=placements.get(parent?.id)??parent?.transform,parentCoordinate=coordinate(parent,parentName),childCoordinate=coordinate(child,childName);
    if(!parentPose||!parentCoordinate||!childCoordinate){diagnostics.push({objectId:child.id,message:'安装端点尚未就绪：'+parentName+' → '+childName});return false;}
    let transform=coordinateInstallation(parentPose,parentCoordinate,childCoordinate);const originalInput=child.frameBinding?project.frameDesign.inputs.find(input=>input.originalObjectId===child.originalDesignBinding?.objectId):null;
    if(originalInput?.originalPreviewPorts){const plan=project.frameDesign.plans.find(row=>row.positionKey===child.frameBinding.positionKey),outlet=coordinate(child,plan.outletCoordinateName);if(!outlet)return false;transform=originalFrameInstallation(originalInput,childCoordinate,outlet);}
    placements.set(child.id,transform);assignments.push({objectId:child.id,transform,parentId:parent.id,parentCoordinateName:parentName,childCoordinateName:childName,originalPlannerPorts:!!originalInput?.originalPreviewPorts});return true;
  };
  for(const scenario of new Set(project.objects.filter(object=>object.baseBinding).map(object=>object.baseBinding.scenario))){
    const record=project.cabinetInputs[scenario],plan=planCabinetBases(scenario,record),cabinet=objectBy(object=>object.cabinetBinding?.scenario===scenario,scenario);
    for(const position of plan.plans){
      const selected=role=>objectBy(object=>object.baseBinding?.scenario===scenario&&object.baseBinding.positionName===position.positionName&&object.baseBinding.role===role,position.positionName+'/'+role),base=selected('base'),elevated=selected('elevated'),rootName=baseRootCoordinateName(scenario,record,position),baseName=scenario==='side-to-side'?position.baseInstallationCoordinateName:'安装坐标系';
      if(position.elevatedBaseModel){const elevatedInstalled=connect(cabinet,rootName,elevated,'下');if(elevatedInstalled)connect(elevated,'上',base,baseName);else if(base)diagnostics.push({objectId:base.id,message:'普通基座等待必需的增高基座安装'});}
      else connect(cabinet,rootName,base,baseName);
    }
  }
  const frames=project.frameDesign;
  if(frames?.plans.length){
    const plan=planCabinetBases(project.scenario,project.cabinetInputs[project.scenario]),candidates=plan.plans.filter(position=>project.scenario==='face-to-wall'||position.positionName.startsWith('B')),requested=frames.basePositionName??frames.context?.basePositionName,anchor=requested?candidates.find(position=>position.positionName===requested):candidates.length===1?candidates[0]:null;
    let parent=anchor?objectBy(object=>object.baseBinding?.scenario===project.scenario&&object.baseBinding.positionName===anchor.positionName&&object.baseBinding.role==='base',anchor.positionName):null,parentName='基座安装坐标系',connected=!!placements.has(parent?.id);
    for(const position of orderedFrameInstallationPlans(frames)){
      const child=objectBy(object=>object.frameBinding?.positionKey===position.positionKey,position.positionKey);
      if(!child){connected=false;continue;}
      if(connected)connected=connect(parent,parentName,child,position.inletCoordinateName);
      else diagnostics.push({objectId:child.id,message:anchor?'等待前段基座或基架安装':'请选择首段基架连接的B柜基座'});
      parent=child;parentName=position.outletCoordinateName;
    }
  }
  for(const support of project.objects.filter(object=>object.structureSupportBinding)){
    const binding=support.structureSupportBinding,parent=project.objects.find(object=>object.id===binding.hostObjectId);
    if(placements.has(parent?.id))connect(parent,binding.coordinateName,support,'安装坐标系');
    else diagnostics.push({objectId:support.id,message:'支撑等待基座或基架宿主安装'});
  }
  return {version:'original-structure-preview-placement/1',assignments,diagnostics};
}
