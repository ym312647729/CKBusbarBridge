import {planCopperPreviewHoles} from './original-copper-holes.mjs';
import {DefinitionError} from './expressions.mjs';
import {createOriginalCopperObject,originalCopperVersion} from './original-copper.mjs';
import {frameTemplatePaths} from './structure-sources.mjs';

export const originalJointInputSignature=project=>JSON.stringify([project.scenario,project.cabinetInputs[project.scenario]]);
export function removeOriginalJointHangers(project){const automaticDrafts=new Set((project.hangerPlans??[]).filter(row=>row.automatic==='original-joint-hangers/1').map(row=>row.id));project.hangerPlans=(project.hangerPlans??[]).filter(row=>!automaticDrafts.has(row.id));project.objects=project.objects.filter(row=>!automaticDrafts.has(row.hangerBinding?.draftId));if(project.originalDesign)delete project.originalDesign.automaticHangers;}
export function invalidateOriginalJointDesign(project){
  removeOriginalJointHangers(project);
  const baseIds=new Set(project.objects.filter(object=>object.baseBinding).map(object=>object.id));
  project.objects=project.objects.filter(object=>object.structureSupportBinding?baseIds.has(object.structureSupportBinding.hostObjectId):!object.originalDesignBinding&&!object.frameBinding);
  delete project.frameDesign;
  project.originalDesign.status='draft';
  project.workflow={...project.workflow,completed:(project.workflow?.completed??[]).filter(step=>step!=='bridge')};
}
export function originalJointField(project,binding){
  if(binding?.kind!=='original-joint')return null;
  if(project.originalDesign?.inputSignature!==originalJointInputSignature(project))throw new DefinitionError('柜体参数已变化，请按原流程重新规划母排与基架');
  const row=project.originalDesign.result.rows.find(row=>row.id===binding.rowId),field=row?.fields.find(field=>field.field===binding.field);
  if(!field||field.readOnly)throw new DefinitionError('该尺寸不属于原界面可修改的业务组');return {row,field};
}
export function applyOriginalJointResult(project,{upstream,commands,result}){
  if(result?.version!=='original-joint-design/1')throw new DefinitionError('原联合设计结果版本无效');
  removeOriginalJointHangers(project);
  const frames=result.objects.filter(row=>row.kind==='Frame').sort((a,b)=>a.framePositionIndex-b.framePositionIndex),inputs=frames.map(frame=>{
    const member=row=>result.objects.find(object=>object.id===row.memberIds[0]);
    // Original drive planners order by local X; the UI orders by bridge station,
    // which runs in the opposite direction on a reversed frame.
    const insulators=result.rows.filter(row=>row.kind==='insulator'&&row.memberIds.some(id=>result.objects.find(object=>object.id===id)?.parentId===frame.id)).sort((left,right)=>member(left).values.X-member(right).values.X||((member(left).insulatorGroupId??'')<(member(right).insulatorGroupId??'')?-1:(member(left).insulatorGroupId??'')>(member(right).insulatorGroupId??'')?1:0));
    const templateIdentity={Regular:'Regular024',SmallOffset:'FaceToFaceSmallOffset032',LargeOffsetCorner:'FaceToFaceLargeOffsetCorner028',SideToSidePhaseChange029:'SideToSidePhaseChange029',FaceToWallLarge1:'FaceToWallLarge1',FaceToWallLarge2:'FaceToWallLarge2'}[frame.frameTemplateKey];
    if(!frameTemplatePaths[templateIdentity])throw new DefinitionError('原联合方案尚未映射基架模板：'+frame.frameTemplateKey);
    const small=frame.smallOffsetPreview;
    const corner=frame.frameTemplateKey==='LargeOffsetCorner',installation=frame.cornerInstallation;
    if(corner&&(!installation||!['1','2'].some(route=>installation.inletCoordinateName==='坐标系-进'+route&&installation.outletCoordinateName==='坐标系-出'+route)||installation.center?.length!==3||!installation.center.every(Number.isFinite)))
      throw new DefinitionError('大偏移拐角缺少原安装路线或中心：'+frame.id);
    if(corner&&![installation.templateWidth,installation.templateLength].every(value=>Number.isFinite(value)&&value>0))
      throw new DefinitionError('大偏移拐角缺少模板局部长宽：'+frame.id);
    const drive=result.frameDrives?.find(row=>row.positionIndex===frame.framePositionIndex&&row.sourceName===frame.sourceName),supportDrive=result.frameInsulatorDrives?.find(row=>row.frameId===frame.id);
    const groups=insulators.map(row=>row.memberIds.map(id=>result.objects.find(object=>object.id===id)));
    const mounts=supportDrive?.mounts??(groups.some(group=>group.some(member=>member.mountPlan))?groups.map(group=>group[0].mountPlan):null),transverseOffsets=supportDrive?.transverseOffsets??(frame.useOffsetInsulators?groups.map(group=>group.reduce((sum,member)=>sum+member.values.Y,0)/group.length):null);
    const support=frame.insulatorAssemblyHeight>25&&frame.insulatorPhaseSpacing>0?{assemblyHeight:frame.insulatorAssemblyHeight,phaseSpacing:frame.insulatorPhaseSpacing,padCount:frame.insulatorPadCount,padThickness:frame.insulatorPadThickness,transverseOffsets:transverseOffsets?.map(value=>Math.sign(value)*Math.round(Math.abs(value)*1000)/1000)??null,mounts:structuredClone(mounts)}:null;
    return {originalObjectId:frame.id,suppressTemplateAngleNotches:frame.suppressTemplateAngleNotches??null,originalPreviewPorts:structuredClone(frame.previewPorts),sourceName:frame.sourceName,kind:corner?'FaceToFaceLargeOffsetCorner':frame.frameTemplateKey==='FaceToWallLarge1'?'Large1':frame.frameTemplateKey==='FaceToWallLarge2'?'Large2':'Regular',templateIdentity,width:corner?installation.templateWidth:drive?.width??frame.values.Width,length:corner?installation.templateLength:drive?drive.length-drive.wallEntryDistance:frame.values.Length,height:drive?.height??frame.values.Height,insulatorCount:drive?.insulatorCount??insulators.length,insulatorPositions:drive?.insulatorPositions??insulators.map(row=>row.fields.find(field=>field.field==='InsulatorPosition').value),originalInsulatorIds:insulators.map(row=>row.id),isReversed:frame.isReversed,...corner?{inletCoordinateName:installation.inletCoordinateName,outletCoordinateName:installation.outletCoordinateName,originalCornerCenter:structuredClone(installation.center)}:{},...support?{originalSupport:support}:{},...drive?{wallEntryDistance:drive.wallEntryDistance,originalDrive:structuredClone(drive)}:{},...small?{smallOffset:{frameWidth:frame.values.Width,inletOffsetDirection:small.InletDirection,inletOffsetSize:small.InletSize,outletOffsetDirection:small.OutletDirection,outletOffsetSize:small.OutletSize}}:{}};
  });
  const copper=[];
  for(const route of result.routes){
    if(!route.nativeGeometry)continue;
    const setting=Object.fromEntries(Object.entries(route.nativeGeometry).map(([name,value])=>[name[0].toLowerCase()+name.slice(1),structuredClone(value)]));setting.version=originalCopperVersion;setting.coordinateFrame='solidworks';setting.pathContract='native-material';
    const object=createOriginalCopperObject(setting,route.phase+'相 · '+(route.generationGroup??route.id),route.phase);object.id=project.id+':original-copper:'+route.id;object.copperGroupId=project.id+':'+(route.copperSectionId??route.generationGroup??'cabinet-connections');object.originalDesignBinding={version:'original-joint-object/1',objectId:route.id,rowId:route.copperSectionId};if(project.settings?.includeCopperHoles&&!result.holePattern)throw new DefinitionError("当前联合预览缺少铜排开孔工艺");if(result.holePattern)object.originalHolePlan=planCopperPreviewHoles(object,result.holePattern,result.insulatorConnections,result.rootCount,(upstream.bushingHoles??[]).map(hole=>({...hole,center:[hole.center[0],-hole.center[2],hole.center[1]],normal:[hole.normal[0],-hole.normal[2],hole.normal[1]],axis:[hole.axis[0],-hole.axis[2],hole.axis[1]]})));copper.push(object);
  }
  project.objects=project.objects.filter(object=>!object.originalDesignBinding&&!object.frameBinding&&!object.structureSupportBinding||object.structureSupportBinding&&project.objects.find(host=>host.id===object.structureSupportBinding.hostObjectId)?.baseBinding);
  project.frameDesign={inputs,...result.frameDriveContext?{originalContext:structuredClone(result.frameDriveContext)}:{}};project.designModules={...project.designModules,bridge:true};
  project.originalDesign={version:'original-joint-project/1',inputSignature:originalJointInputSignature(project),upstream:structuredClone(upstream),commands:structuredClone(commands),result:structuredClone(result),status:result.status};project.objects.push(...copper);
}
export function resolveOriginalJointBindings(project,objects,frames){
  const design=project.originalDesign;if(!design)return objects;
  if(design.version!=='original-joint-project/1'||design.result?.version!=='original-joint-design/1'||!Array.isArray(design.commands))throw new DefinitionError('原联合设计保存状态无效');
  if(design.inputSignature!==originalJointInputSignature(project))design.status='draft';
  const frameInputs=frames?.inputs??[];
  return objects.map(object=>{
    if(object.frameBinding){const position=frames.plans.find(row=>row.positionKey===object.frameBinding.positionKey),input=frameInputs[position.positionIndex-1];if(input.originalObjectId)return {...object,originalDesignBinding:{version:'original-joint-object/1',objectId:input.originalObjectId,rowId:input.originalObjectId}};}
    if(object.structureSupportBinding){const host=objects.find(row=>row.id===object.structureSupportBinding.hostObjectId),position=frames?.plans.find(row=>row.positionKey===host?.frameBinding?.positionKey),index=Number(/坐标系(\d+)$/.exec(object.structureSupportBinding.coordinateName)?.[1])-1,input=position?frameInputs[position.positionIndex-1]:null,rowId=input?.originalInsulatorIds?.[index];if(rowId){const row=design.result.rows.find(row=>row.id===rowId);if(!row?.memberIds?.length)throw new DefinitionError('绝缘子组缺少原设计成员身份：'+rowId);return {...object,originalDesignBinding:{version:'original-joint-object/1',objectId:rowId,rowId,memberIds:row.memberIds}};}}
    return object;
  });
}
