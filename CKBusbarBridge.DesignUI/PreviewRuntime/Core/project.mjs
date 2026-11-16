import {DefinitionError} from './expressions.mjs';
import {validateRouteDraft} from './path-planning.mjs';
import {validateInstallationMatrix} from './installation-pose.mjs';
import {endpointReferenceIssue} from './template-interfaces.mjs';
import {validateInsulatorDraft} from './insulator-planning.mjs';
import {validateHangerDraft,hangerReferenceIssues} from './hanger-draft.mjs';
import {resolveHangerInstances} from './hanger-installation.mjs';
import {validatedClearanceState} from './clearance-state.mjs';
import {validateFreeCopper} from './free-copper.mjs';
import {validateOriginalCopperObject} from './original-copper.mjs';
import {validateCopperLaps} from './copper-lap.mjs';
import {validateOriginalCabinetInputs,createOriginalCabinetInput} from './original-cabinet.mjs';
import {resolveCabinetSceneInstances} from './cabinet-binding.mjs';
import {planCabinetBases} from './cabinet-base-planning.mjs';
import {resolveInsulatorInstances} from './insulator-binding.mjs';
import {validateProjectFrameDesign} from './frame-design.mjs';
import {resolveFramePreviewInstances} from './frame-binding.mjs';
import {resolveBasePreviewInstances} from './base-binding.mjs';
import {resolveStructureSupportInstances} from './structure-support-binding.mjs';
import {resolveOriginalJointBindings,originalJointInputSignature,invalidateOriginalJointDesign} from './original-joint-design.mjs';
import {structurePreviewPlacements} from './structure-preview-placement.mjs';
import {normalizeDesignWorkflow,normalizePreviewSettings,currentPresentationVersion} from './preview-presentation.mjs';
import {resolveDesignModuleObjects} from './design-modules.mjs';
export const categoryNames={cabinet:'柜体',base:'独立基座',raisedBase:'增高基座',frame:'中间基架',shell:'外壳 / 盖板',copper:'铜排',insulator:'绝缘子',bracket:'支架',pad:'垫块',hanger:'吊杆',wall:'墙端接口'};
export const designSteps=[['cabinet','柜体设计'],['bridge','母排与基架设计']];
export function createProject(scenario='face-to-face'){
  return {schemaVersion:'dq-bridge-project/1',id:crypto.randomUUID(),name:'未命名母线桥设计',scenario,revision:0,cabinetInputs:{[scenario]:createOriginalCabinetInput(scenario)},objects:[],routes:[],supportPlans:[],hangerPlans:[],settings:{presentationVersion:currentPresentationVersion,transparency:0,frameTransparency:0,roofTransparency:.5,beamTransparency:.5,partTransparency:{},arcSegments:24,chordToleranceMm:.3,objectTriangleBudget:80000,sceneTriangleBudget:700000,targetFps:60,interactionFrameBudgetMs:1000/30},workflow:{step:'cabinet',completed:[]},templates:{}};
}
export function resetScenarioDesign(project,scenario){
  const input=project.cabinetInputs?.[scenario]??createOriginalCabinetInput(scenario);
  if(!['face-to-face','face-to-wall','side-to-side'].includes(scenario))throw new DefinitionError('场景无效');
  project.scenario=scenario;
  project.cabinetInputs??={};project.cabinetInputs[scenario]=input;
  project.objects=[];project.routes=[];project.supportPlans=[];project.hangerPlans=[];project.copperLaps=[];project.templates={};
  project.workflow={step:project.workflow.step,completed:[]};
  project.settings.partTransparency={};
  for(const key of ['designModules','originalDesign','frameDesign','hangerClearance','copperClearance'])delete project[key];
}
export function validateProject(project){
  if(project?.schemaVersion!=='dq-bridge-project/1')throw new DefinitionError('项目文件版本不支持');
  if(project.originalDesign&&project.originalDesign.inputSignature!==originalJointInputSignature(project)){project=structuredClone(project);invalidateOriginalJointDesign(project);}
  if(!['face-to-face','face-to-wall','side-to-side'].includes(project.scenario))throw new DefinitionError('场景无效');
  const validatedCabinetInputs=validateOriginalCabinetInputs(project.cabinetInputs);
  const validatedFrameDesign=validateProjectFrameDesign(project.scenario,validatedCabinetInputs[project.scenario],project.frameDesign);
  if(project.designModules?.cabinet)project={...project,cabinetInputs:validatedCabinetInputs,objects:resolveDesignModuleObjects(project,validatedFrameDesign)};
  if(!Array.isArray(project.objects)||!Array.isArray(project.routes))throw new DefinitionError('项目对象结构无效');
  if(project.supportPlans!==undefined&&!Array.isArray(project.supportPlans))throw new DefinitionError('绝缘子规划结构无效');
  if(project.hangerPlans!==undefined&&!Array.isArray(project.hangerPlans))throw new DefinitionError('吊杆规划结构无效');
  const previewSettings=project.settings;
  if(!previewSettings||typeof previewSettings!=='object')throw new DefinitionError('缺少预览设置');
  if(!Number.isFinite(previewSettings.transparency)||previewSettings.transparency<0||previewSettings.transparency>1)throw new DefinitionError('透明度必须为0至100%');
  for(const key of ['frameTransparency','roofTransparency','beamTransparency'])if(previewSettings[key]!==undefined&&(!Number.isFinite(previewSettings[key])||previewSettings[key]<0||previewSettings[key]>1))throw new DefinitionError('分类透明度必须为0至100%');
  if(previewSettings.partTransparency!==undefined&&(typeof previewSettings.partTransparency!=='object'||previewSettings.partTransparency===null||Array.isArray(previewSettings.partTransparency)||Object.values(previewSettings.partTransparency).some(value=>!Number.isFinite(value)||value<0||value>1)))throw new DefinitionError('零件透明度无效');
  for(const key of ['arcSegments','complexArcSegments'])if((key==='arcSegments'||previewSettings[key]!==undefined)&&(!Number.isInteger(previewSettings[key])||previewSettings[key]<12||previewSettings[key]>720))throw new DefinitionError('圆弧分段数必须为12至720之间的整数');
  for(const key of ['objectTriangleBudget','sceneTriangleBudget'])if(!Number.isInteger(previewSettings[key])||previewSettings[key]<1)throw new DefinitionError('面片预算必须为正整数');
  if(!Number.isFinite(previewSettings.chordToleranceMm)||previewSettings.chordToleranceMm<=0)throw new DefinitionError('轮廓弦差必须为正数');
  if(!Number.isFinite(previewSettings.targetFps)||previewSettings.targetFps<1||previewSettings.targetFps>240)throw new DefinitionError('目标刷新率必须为1至240之间的数值');
  if(previewSettings.interactionFrameBudgetMs!==undefined&&(!Number.isFinite(previewSettings.interactionFrameBudgetMs)||previewSettings.interactionFrameBudgetMs<=0))throw new DefinitionError('交互帧预算必须为有限正数');
  const identifiers=new Set();
  for(const object of project.objects){
    if(!object.id||identifiers.has(object.id))throw new DefinitionError('对象标识缺失或重复');identifiers.add(object.id);
    if(!categoryNames[object.category])throw new DefinitionError('对象类别无效');
    if(object.category==='base'||object.category==='raisedBase'||object.category==='frame')if(object.parentId)throw new DefinitionError('基座和中间基架必须直属总装');
    for(const parameterValue of Object.values(object.parameters??{}))if(typeof parameterValue!=='number'||!Number.isFinite(parameterValue))throw new DefinitionError('尺寸必须是有限数');
    validateInstallationMatrix(object.transform);
    if(object.kind==='free-copper'){
      if(![null,'A','B','C'].includes(object.phase??null))throw new DefinitionError('铜排相别无效');
      if(object.category!=='copper'||object.templatePath!=='free-draw/busbar'||object.configuration!=='自由绘制'||object.sourceSha256||Object.keys(object.suppression??{}).length)throw new DefinitionError('自由铜排身份或驱动状态无效');
      validateFreeCopper(object.freeCopper,object.parameters);
      validateOriginalCopperObject(object);
    }else if(object.kind==='original-copper'){
      if(object.category!=='copper'||object.templatePath!=='free-draw/original-busbar'||object.configuration!=='原方案绘制'||object.sourceSha256||Object.keys(object.suppression??{}).length||![null,'A','B','C'].includes(object.phase??null))throw new DefinitionError('原混合工艺铜排身份无效');
      validateOriginalCopperObject(object);
    }else if(object.freeCopper)throw new DefinitionError('自由铜排定义不能冒充原生模板');
    if(object.originalCopper&&!['free-copper','original-copper'].includes(object.kind))throw new DefinitionError('原方案铜排定义不能附加到其他模型');
    if(!object.templatePath||object.templatePath.includes('..')||object.templatePath.startsWith('/')||object.templatePath.includes(':'))throw new DefinitionError('模板相对路径无效');
    if(typeof object.configuration!=='string'||!object.configuration)throw new DefinitionError('对象配置缺失');
  }
  for(const object of project.objects){
    if(object.parentId&&!identifiers.has(object.parentId))throw new DefinitionError('对象的父级不存在');
    const ancestry=new Set([object.id]);let parentId=object.parentId;
    while(parentId){if(ancestry.has(parentId))throw new DefinitionError('对象层级形成循环');ancestry.add(parentId);parentId=project.objects.find(item=>item.id===parentId)?.parentId;}
  }
  const validatedRoutes=project.routes.map(route=>{if(identifiers.has(route.id))throw new DefinitionError('路径标识重复');identifiers.add(route.id);return validateRouteDraft(route,project.objects);});
  const validatedSupports=(project.supportPlans??[]).map(draft=>{if(identifiers.has(draft.id))throw new DefinitionError('绝缘子规划标识重复');identifiers.add(draft.id);return validateInsulatorDraft(draft);});
  const hangerFrames=new Set(),validatedHangers=(project.hangerPlans??[]).map(draft=>{if(identifiers.has(draft.id))throw new DefinitionError('吊杆规划标识重复');identifiers.add(draft.id);const validated=validateHangerDraft(draft,project.objects);for(const plan of validated.plans){const key=validated.automatic&&validated.input.frames.some(frame=>frame.localOffset!==undefined)?validated.id:plan.pose.frameId;if(hangerFrames.has(key))throw new DefinitionError('多个吊杆规划重复绑定同一位置');hangerFrames.add(key);}return validated;});
  const objects=resolveOriginalJointBindings(project,resolveStructureSupportInstances(resolveBasePreviewInstances(resolveFramePreviewInstances(resolveCabinetSceneInstances(resolveHangerInstances(resolveInsulatorInstances(structuredClone(project.objects),validatedSupports),validatedHangers),validatedCabinetInputs),validatedFrameDesign),validatedCabinetInputs),validatedCabinetInputs,validatedFrameDesign,project.scenario),validatedFrameDesign);
  const validatedLaps=validateCopperLaps(project.copperLaps,objects);for(const lap of validatedLaps){if(identifiers.has(lap.id))throw new DefinitionError('搭接标识重复');identifiers.add(lap.id);}
  for(const draft of validatedHangers){const installed=objects.filter(object=>object.hangerBinding?.draftId===draft.id);draft.installedObjectIds=installed.map(object=>object.id);if(installed.length===draft.plans.length){draft.pending=draft.automatic?[]:['吊杆及周边净距校验'];if(draft.automatic)draft.status='designed';}}
  const validated={...structuredClone(project),settings:structuredClone(normalizePreviewSettings(previewSettings)),workflow:normalizeDesignWorkflow(project.workflow),cabinetInputs:validatedCabinetInputs,objects,routes:validatedRoutes,supportPlans:validatedSupports,hangerPlans:validatedHangers,copperLaps:validatedLaps};if(validatedFrameDesign)validated.frameDesign=validatedFrameDesign;validated.hangerClearance=validatedClearanceState(validated);validated.copperClearance=validatedClearanceState(validated,'copper');return validated;
}
export const identityMatrix=()=>[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1];
export function createObject(template,category,parentId=null,configuration=template.configuration){
  if(!categoryNames[category])throw new DefinitionError('请选择设计类别');
  if(!configuration||template.configurations&&!template.configurations.includes(configuration))throw new DefinitionError('所选模板配置不存在');
  if(template.configurationDetails?.find(detail=>detail.name===configuration)?.canAdd===false)throw new DefinitionError('所选配置按用户要求暂缓或尚未采集');
  return {id:crypto.randomUUID(),name:template.name,category,parentId,templatePath:template.path,configuration,sourceSha256:template.sha256??null,parameters:{},suppression:{},transform:identityMatrix(),visible:true};
}
export class ProjectSession{
  constructor(project=createProject()){this.project=validateProject(project);this.undoStack=[];this.redoStack=[];this.saved=JSON.stringify(this.project);}
  get dirty(){return JSON.stringify(this.project)!==this.saved;}
  commit(operation,{recordUndo=true}={}){const previous=structuredClone(this.project);const next=structuredClone(this.project);operation(next);next.revision=this.project.revision+1;this.project=validateProject(next);if(recordUndo){this.undoStack.push(previous);if(this.undoStack.length>150)this.undoStack.shift();this.redoStack=[];}return this.project;}
  undo(){if(!this.undoStack.length)return false;this.redoStack.push(this.project);const revision=this.project.revision+1;this.project=this.undoStack.pop();this.project.revision=revision;return true;}
  redo(){if(!this.redoStack.length)return false;this.undoStack.push(this.project);const revision=this.project.revision+1;this.project=this.redoStack.pop();this.project.revision=revision;return true;}
  replace(project){const replacement=validateProject(project);if(replacement.id===this.project.id)replacement.revision=Math.max(replacement.revision,this.project.revision+1);this.project=replacement;this.undoStack=[];this.redoStack=[];this.markSaved();}
  markSaved(){this.saved=JSON.stringify(this.project);}
  editParameter(objectId,parameterName,value){
    if(!Number.isFinite(value))throw new DefinitionError('输入尺寸无效');
    this.commit(project=>{const object=project.objects.find(item=>item.id===objectId);if(!object)throw new DefinitionError('对象已不存在');object.parameters[parameterName]=value;});
  }
  removeObject(objectId){this.commit(project=>{const removedIds=new Set([objectId]);for(let index=0;index<project.objects.length;index++)for(const object of project.objects)if(removedIds.has(object.parentId))removedIds.add(object.id);project.objects=project.objects.filter(object=>!removedIds.has(object.id));});}
  changeConfiguration(objectId,template,configuration){
    if(!template.configurations?.includes(configuration))throw new DefinitionError('所选模板配置不存在');
    if(template.configurationDetails?.find(detail=>detail.name===configuration)?.canAdd===false)throw new DefinitionError('所选配置按用户要求暂缓或尚未采集');
    this.commit(project=>{const object=project.objects.find(item=>item.id===objectId);if(!object||object.templatePath!==template.path)throw new DefinitionError('配置不属于当前对象');object.configuration=configuration;object.parameters={};object.suppression={};});
  }
}
export function geometrySignature(object){return JSON.stringify([object.templatePath,object.configuration,object.sourceSha256,Object.entries(object.parameters).sort(),Object.entries(object.suppression).sort(),object.hangerDimensions??null,object.hangerBinding?.templateReference??null,object.freeCopper??null,object.originalCopper??null,object.originalHolePlan??null,object.structurePlanInput??null,object.cabinetBinding??null,object.cabinetSceneInput??null,object.insulatorPlanInput??null]);}
export function inspectCompletion(project,geometryResults){
  const issues=[];
  const originalInputs=project.cabinetInputs?.[project.scenario];
  if(originalInputs){try{planCabinetBases(project.scenario,originalInputs);}catch(error){issues.push({severity:'error',message:'原基座联动参数：'+error.message});}}
  else issues.push({severity:'error',message:'缺少当前场景的原项目输入参数'});
  if(!project.objects.some(object=>object.cabinetBinding?.scenario===project.scenario))issues.push({severity:'error',message:'尚未生成柜体设计'});
  if(!project.frameDesign?.plans.length)issues.push({severity:'error',message:'尚未定义母排基架设计'});
  for(const route of project.routes)if(route.status==='planning')issues.push({severity:'info',routeId:route.id,message:route.name+'：保留的路径草稿，单独绘制时继续处理'});
  if(project.frameDesign?.plans.some(position=>!project.objects.some(object=>object.frameBinding?.positionKey===position.positionKey)))issues.push({severity:'error',message:'部分中间基架只有参数组，尚未生成安装预览'});
  try{const placements=structurePreviewPlacements(project,geometryResults);for(const diagnostic of placements.diagnostics)issues.push({severity:'error',...diagnostic});for(const placement of placements.assignments){const object=project.objects.find(object=>object.id===placement.objectId);if(object.transform.some((value,index)=>Math.abs(value-placement.transform[index])>1e-5))issues.push({severity:'error',objectId:object.id,message:object.name+'安装位置等待更新'});}}catch(error){issues.push({severity:'error',message:error.message});}
  for(const object of project.objects){const result=geometryResults.get(object.id);if(!result?.ok)issues.push({severity:'warning',objectId:object.id,message:object.name+'：示意预览'+(result?.error??'尚未生成')});
    const sourceState=result?.sourceSupport?.assemblySourceState;if(sourceState&&(sourceState.missingVisibility||sourceState.missingEnvelopeState))issues.push({severity:'error',objectId:object.id,message:object.name+'：原生装配组件状态待补采，当前显示全部未压缩且未暂缓的组件'});
  }
  if(!originalInputs&&project.scenario!=='face-to-wall'&&project.objects.filter(object=>object.category==='cabinet').reduce((sum,object)=>sum+(object.cabinetBinding?.scenario===project.scenario?2:1),0)<2)issues.push({severity:'error',message:'两柜场景缺少A柜或B柜'});
  for(const support of project.supportPlans??[])if(!project.objects.some(object=>object.insulatorBinding?.planId===support.id))issues.push({severity:'error',supportId:support.id,message:support.name+'：布局已保存，尚未生成安装组预览'});
  for(const draft of project.hangerPlans??[]){if(draft.pending.length)issues.push({severity:'error',hangerId:draft.id,message:draft.name+'：安装草稿已保存，'+draft.pending.join('、')+'尚未完成'});for(const message of hangerReferenceIssues(draft,geometryResults))issues.push({severity:'error',hangerId:draft.id,message:draft.name+'：'+message});}
  const clearance=project.hangerClearance?.report;if(clearance)issues.push({severity:'info',message:`可选吊杆预览净距：${clearance.pairs.length}对，${clearance.conflicts}项接触或冲突`});
  const copperClearance=project.copperClearance?.report;if(copperClearance)issues.push({severity:'info',message:`可选铜排预览净距：${copperClearance.pairs.length}对，${copperClearance.conflicts}项接触或冲突`});
  for(const lap of project.copperLaps??[])issues.push({severity:'info',lapId:lap.id,message:lap.name+'：搭接及成对孔参数已保存'});
  for(const route of project.routes)for(const [side,reference]of Object.entries(route.input.endpointReferences??{})){const problem=endpointReferenceIssue(reference,geometryResults.get(reference.objectId));if(problem)issues.push({severity:'error',routeId:route.id,objectId:reference.objectId,message:route.name+' '+(side==='start'?'B端':'A端')+'：'+problem});}
  for(const object of project.objects){const result=geometryResults.get(object.id);if(result?.deferred?.length)issues.push({severity:'info',objectId:object.id,message:object.name+'：螺柱按用户要求暂缓，保留已实现组件'});}
  return issues;
}
