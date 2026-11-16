import {DefinitionError} from './expressions.mjs';
import {evaluateTemplate} from './geometry.mjs';
import {evaluateCopperObject} from './original-copper.mjs';
import {deriveStructureAssembly} from './structure-assembly.mjs';
import {inspectCopperLap} from './copper-lap.mjs';
import {hangerTemplate} from './fixed-structure-templates.mjs';
import {hangerRules} from './hanger-planning.mjs';
import {deriveFixedStructureDatums,generateFromFixedDatums,hasFixedStructureDatums} from './fixed-structure-datums.mjs';
import {deriveCabinetScene} from './cabinet-scene.mjs';
import {appendFixedCabinetCopper} from './cabinet-fixed-copper.mjs';
import {evaluateInsulatorDraftPreview,evaluateInsulatorDesign} from './insulator-preview.mjs';
import {fixedHangerDatumDefinition,hangerTemplateReference} from './hanger-installation.mjs';
import {clearanceVersionFor,validateMinimumGap,clearanceInputSignature} from './clearance-state.mjs';

// Bounding boxes only reject disjoint search ranges. Actual overlap and distance use
// the constructed solids, including their openings; boxes never stand in for geometry.
function boxSeparation(first,second){return Math.hypot(...[0,1,2].map(axis=>Math.max(0,first.min[axis]-second.max[axis],second.min[axis]-first.max[axis])));}
export function measureSolidSets(first,second,minimumGapMm,searchLengthMm=Math.max(100,minimumGapMm+1)){
  validateMinimumGap(minimumGapMm);if(!Number.isFinite(searchLengthMm)||searchLengthMm<=minimumGapMm)throw new DefinitionError('净距搜索范围必须大于要求值');
  if(!first.length||!second.length)throw new DefinitionError('净距对象没有可检查的物理实体');
  let gap=searchLengthMm,overlapVolumeMm3=0,solidPairs=0;const numericalContacts=[];
  const firstBoxes=first.map(solid=>solid.boundingBox()),secondBoxes=second.map(solid=>solid.boundingBox());
  for(const [leftIndex,left]of first.entries())for(const [rightIndex,right]of second.entries()){
    if(left.status()!=='NoError'||right.status()!=='NoError')throw new DefinitionError('净距检查实体无效');
    if(boxSeparation(firstBoxes[leftIndex],secondBoxes[rightIndex])>searchLengthMm)continue;
    solidPairs++;const common=left.intersect(right);
    try{
      if(common.status()!=='NoError')throw new DefinitionError('实体交集计算失败');const volume=common.volume();if(!Number.isFinite(volume))throw new DefinitionError('实体交集体积无效');
      if(!common.isEmpty()){
        const bounds=common.boundingBox(),thicknessMm=Math.min(...[0,1,2].map(axis=>bounds.max[axis]-bounds.min[axis])),toleranceMm=common.tolerance(),surfaceAreaMm2=common.surfaceArea();
        if(thicknessMm>=0&&thicknessMm<=toleranceMm&&Math.abs(volume)<=surfaceAreaMm2*toleranceMm)numericalContacts.push({leftIndex,rightIndex,signedVolumeMm3:volume,thicknessMm,toleranceMm,surfaceAreaMm2});
        else{if(volume<0)throw new DefinitionError('实体交集出现超出精度界的负体积：'+volume);overlapVolumeMm3+=volume;}
      }
    }finally{common.delete();}
    const candidate=left.minGap(right,searchLengthMm);if(!Number.isFinite(candidate)||candidate<0)throw new DefinitionError('实体净距计算失败');gap=Math.min(gap,candidate);
  }
  const status=overlapVolumeMm3>0?'overlap':numericalContacts.length?'numerical-contact':gap<=1e-6?'contact':gap<minimumGapMm?'below-minimum':'clear';
  return {status,gapMm:overlapVolumeMm3>0?0:gap,gapIsLowerBound:!overlapVolumeMm3&&gap>=searchLengthMm,overlapVolumeMm3,numericalContacts,solidPairs,searchLengthMm};
}

export const checkHangerClearance=(kernel,project,readSource,minimumGapMm,onProgress)=>checkPhysicalClearance(kernel,project,readSource,minimumGapMm,'hanger',onProgress);
export const checkCopperClearance=(kernel,project,readSource,minimumGapMm,onProgress)=>checkPhysicalClearance(kernel,project,readSource,minimumGapMm,'copper',onProgress);
async function checkPhysicalClearance(kernel,project,readSource,minimumGapMm,category,onProgress=()=>{}){
  validateMinimumGap(minimumGapMm);
  const started=performance.now(),inputSignature=clearanceInputSignature(project,minimumGapMm,category),subjects=project.objects.filter(object=>object.category===category);
  if(!subjects.length)throw new DefinitionError(category==='copper'?'请先创建铜排实体再检查净距':'请先安装真实吊杆再检查净距');
  const blockers=[],pairs=[],prepared=new Map(),settings={arcSegments:project.settings.arcSegments,complexArcSegments:project.settings.complexArcSegments??project.settings.arcSegments,chordToleranceMm:project.settings.chordToleranceMm,componentScope:'all-physical'},readStarted=performance.now();
  if(project.cabinetInputs?.[project.scenario]&&project.objects.some(object=>object.category==='cabinet'))blockers.push({message:'原项目柜体参数尚未驱动当前柜体、开孔与基座实体，检查只覆盖已构造的对象'});
  for(const route of project.routes??[])if(route.status==='planning')blockers.push({routeId:route.id,message:route.name+'仅有路径草稿，尚无物理铜排'});
  for(const draft of project.supportPlans??[])blockers.push({supportId:draft.id,message:draft.name+'仅有布局草稿，尚无完整支撑安装'});
  for(const lap of project.copperLaps??[]){const state=inspectCopperLap(lap,project.objects);if(state.status!=='current')blockers.push({lapId:lap.id,message:lap.name+'：'+state.message});}
  for(const draft of project.hangerPlans??[])if(draft.installedObjectIds?.length!==draft.plans.length)blockers.push({hangerId:draft.id,message:draft.name+'尚未安装全部吊杆'});
  try{
    for(const [index,object]of project.objects.entries()){
      let result,worldSolids=[];
      try{
        if(Object.keys(object.suppression??{}).length)throw new DefinitionError('对象压缩修改尚无可验证的物理实体求解');
        if(['free-copper','original-copper'].includes(object.kind))result=evaluateCopperObject(kernel,object,settings);
        else if(object.hangerDimensions){
          const current=hangerTemplateReference(fixedHangerDatumDefinition(),object.configuration),saved=object.hangerBinding?.templateReference;
          if(JSON.stringify(current)!==JSON.stringify(saved))throw new DefinitionError('吊杆安装坐标系记录不符');
          result=hangerTemplate(kernel,object.hangerDimensions.width,
            object.hangerDimensions.angleLength+hangerRules.fixedHeight,current.localTransform);
        }
        else{
        let template=await readSource(object.templatePath),loader=readSource,fixedCopper=null,fixedDatums=null;
        if(template.source.sha256!==object.sourceSha256)throw new DefinitionError('物理检查的模板来源记录不符');
        if(object.cabinetBinding){const derived=await deriveCabinetScene(template,object.configuration,object.cabinetBinding.scenario,object.cabinetSceneInput,readSource);template=derived.template;loader=derived.loadTemplate;fixedCopper=derived.fixedCopper;}
        if(object.frameBinding||object.baseBinding){
          const input=object.structurePlanInput;
          if(hasFixedStructureDatums(object.templatePath))fixedDatums=await deriveFixedStructureDatums(template,object.configuration,input,readSource);
          else{const derived=await deriveStructureAssembly(template,object.configuration,input.globalVariables,readSource,{rootDimensions:input.rootDimensions,businessDrive:input.businessDrive});template=derived.template;loader=derived.loadTemplate;}
          blockers.push({objectId:object.id,message:object.name+'为结构示意，净距结果仅供预览参考'});
        }
        result=fixedDatums?generateFromFixedDatums(kernel,fixedDatums):object.structureSupportBinding?await evaluateInsulatorDesign(kernel,object.insulatorPlanInput,readSource,settings):object.insulatorBinding?await evaluateInsulatorDraftPreview(kernel,object.insulatorPlanInput,readSource,settings):await evaluateTemplate(kernel,template,object.configuration,object.parameters,settings,loader);
        if(fixedCopper)appendFixedCabinetCopper(kernel,result,fixedCopper,settings);
        }
        if(result.deferred?.length)blockers.push({objectId:object.id,message:object.name+'包含暂缓零件，检查只覆盖已构造实体'});
        if(result.schematic?.length)blockers.push({objectId:object.id,message:object.name+'含示意特征，不能据此验收实际净距'});
        for(const solid of result.solids)worldSolids.push(solid.transform(object.transform));
        if(!worldSolids.length)throw new DefinitionError('对象没有已构造的物理实体');
        prepared.set(object.id,{solids:worldSolids});worldSolids=[];
      }catch(error){blockers.push({objectId:object.id,message:object.name+'：'+error.message});}
      finally{result?.solids.forEach(solid=>solid.delete());worldSolids.forEach(solid=>solid.delete());}
      onProgress({stage:'geometry',completed:index+1,total:project.objects.length});
    }
    const geometryMs=performance.now()-readStarted,checkedPairs=new Set(),measureStarted=performance.now();
    for(const subject of subjects)for(const other of project.objects){
      if(subject.id===other.id)continue;const key=JSON.stringify([subject.id,other.id].sort());if(checkedPairs.has(key))continue;checkedPairs.add(key);
      const first=prepared.get(subject.id),second=prepared.get(other.id),draft=project.hangerPlans?.find(row=>row.id===subject.hangerBinding?.draftId),installationInterface=other.id===subject.parentId?'frame':other.id===draft?.input.roof.objectId?'roof':null;
      const pair={subjectId:subject.id,subjectName:subject.name,subjectCategory:category,hangerId:subject.id,objectId:other.id,hangerName:subject.name,objectName:other.name,installationInterface};
      if(!first||!second)pairs.push({...pair,status:'unavailable',message:'实体未完整构造，不能判断此对净距'});
      else{try{pairs.push({...pair,...measureSolidSets(first.solids,second.solids,minimumGapMm)});}catch(error){pairs.push({...pair,status:'unavailable',message:error.message});blockers.push({objectId:other.id,message:'净距对计算失败：'+subject.name+' / '+other.name+'：'+error.message});}}
      onProgress({stage:'distance',completed:pairs.length});
    }
    const conflicts=pairs.filter(pair=>['overlap','contact','numerical-contact','below-minimum'].includes(pair.status)).length;
    const jointChecks=category==='copper'?(project.copperLaps??[]).map(lap=>{
      const state=inspectCopperLap(lap,project.objects),ids=[lap.input?.fixedObjectId,lap.input?.movingObjectId],pair=pairs.find(row=>ids.includes(row.subjectId)&&ids.includes(row.objectId));
      const contactObserved=state.status==='current'&&pair&&['contact','numerical-contact'].includes(pair.status)&&pair.overlapVolumeMm3===0;
      return {lapId:lap.id,name:lap.name,status:state.status!=='current'?'stale':!pair||pair.status==='unavailable'?'unavailable':contactObserved?'contact-observed':'contact-not-confirmed',holePairs:state.evidence?.holePairs??0,phase:state.evidence?.phase??null,gapMm:pair?.gapMm??null,overlapVolumeMm3:pair?.overlapVolumeMm3??null,method:'端头坐标与成对孔参数重算，再检查实际离散实体交集及最短间距；只确认检测到贴合，不自动豁免净距冲突'};
    }):[];
    for(const joint of jointChecks)if(joint.status!=='contact-observed')blockers.push({lapId:joint.lapId,message:joint.name+'：搭接贴合尚未在当前实体上确认'});
    return {version:clearanceVersionFor(category),subjectCategory:category,date:new Date().toISOString(),inputSignature,revision:project.revision,minimumGapMm,pairs,blockers,jointChecks,conclusion:blockers.length||pairs.some(pair=>pair.status==='unavailable')?'incomplete':conflicts?'conflicts':'no-conflicts-in-discretization',conflicts,geometryMs,measureMs:performance.now()-measureStarted,totalMs:performance.now()-started,scope:'全部对象和隐藏物理组件；按来源排除封套；不自动豁免铜排搭接、基架或屋面安装接触',approximation:'按当前圆弧分段及弦差构造的离散实体；不做预览预算降级。数值用于诊断，曲面误差界及模板驱动后的几何等价性尚未核验，不能作为工程净距验收。'};
  }finally{for(const row of prepared.values())row.solids.forEach(solid=>solid.delete());}
}


