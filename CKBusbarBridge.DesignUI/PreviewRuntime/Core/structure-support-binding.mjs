import {DefinitionError} from './expressions.mjs';
import {planCabinetBases} from './cabinet-base-planning.mjs';
import {baseInsulatorInput,universalInsulatorTemplatePath} from './insulator-design.mjs';
import {universalInsulatorDrivePlan,layoutFromCopper,createInsulatorDraft} from './insulator-planning.mjs';
import sources from '../catalog/structure-scene-sources.json' with {type:'json'};
export function frameInsulatorInput(scenario,values,model,index=0){
  const leftRight=scenario==='side-to-side'&&values['General.CabinetTopOpeningMode']==='LeftRight',useLBracket=model.kind==='FaceToFaceLargeOffsetCorner';
  const span=leftRight?model.height:model.width,input={...baseInsulatorInput({hostModelKind:'Frame',bracketHostSpan:span,phaseSpacing:model.originalSupport?.phaseSpacing??values[scenario==='face-to-face'?'General.PhaseSpacing':'General.ExposedBusbarPhaseSpacing'],insulatorHeight:(model.originalSupport?.assemblyHeight??values['General.InsulatorHeight'])-(useLBracket?6:0),singlePhaseRootCount:values['General.SinglePhaseRootCount'],busbarSpec:values['General.BusbarSpec']}),useLBracket},mount=model.originalSupport?.mounts?.[index];
  if(mount){if(useLBracket)throw new DefinitionError('L型支架不支持原逐相安装计划');return {layoutMode:'individual-groups',bracketLength:span-20,groupLocalZ:structuredClone(mount.Transverse),bodyHeights:structuredClone(mount.BodyHeights),phases:structuredClone(mount.Phases),nominalAssemblyHeight:mount.NominalAssemblyHeight,isFloorStanding:mount.IsFloorStanding??false,useLBracket:false,barThickness:input.barThickness,rootCount:input.rootCount};}
  const offset=model.originalSupport?.transverseOffsets?.[index];if(offset!==undefined){if(useLBracket)throw new DefinitionError('L型支架不支持原普通落地横移');input.layout=layoutFromCopper(span-20,model.originalSupport.phaseSpacing,offset);}
  return input;
}
export function frameInsulatorCoordinate(input,index){return '绝缘子'+(input.layoutMode&&!input.isFloorStanding?'吊装':'支架')+'坐标系'+(index+1);}
export function structureSupportCoordinate(input){return [input.useLBracket?-1:1,0,0,0,0,1,0,0,0,0,input.useLBracket?-1:1,0,0,-25,0,1];}
export function resolveStructureSupportInstances(objects,inputs,frames,scenario){
  const source=sources.models.find(source=>source.path===universalInsulatorTemplatePath),existing=new Map(),plans=new Map(),result=objects.filter(object=>!object.structureSupportBinding),ids=new Set(result.map(object=>object.id));
  for(const object of objects.filter(object=>object.structureSupportBinding)){
    const binding=object.structureSupportBinding;
    if(binding.version!=='original-structure-support/1'||Object.keys(binding).some(name=>!['version','hostObjectId','coordinateName'].includes(name))||object.kind!=='structure-insulator-group'||object.category!=='insulator'||object.parentId||object.insulatorBinding||object.templatePath!==universalInsulatorTemplatePath||object.configuration!=='默认'||object.sourceSha256!==source?.sha256||Object.keys(object.parameters??{}).length||Object.keys(object.suppression??{}).length)throw new DefinitionError('结构支撑组未对应原宿主及通用绝缘子');
    const key=JSON.stringify([binding.hostObjectId,binding.coordinateName]);if(existing.has(key))throw new DefinitionError('结构支撑安装点重复');existing.set(key,object);
  }
  function add(host,name,input){
    if(!source)throw new DefinitionError('结构支撑缺少已存通用绝缘子身份');const plan=input.layoutMode?createInsulatorDraft(input):universalInsulatorDrivePlan(input.layout,input),key=JSON.stringify([host.id,name]),previous=existing.get(key),id=previous?.id??host.id+':support:'+name;
    if(ids.has(id))throw new DefinitionError('结构支撑标识与其他对象冲突');ids.add(id);
    result.push({id,name:host.name+' · '+name,kind:'structure-insulator-group',category:'insulator',parentId:null,templatePath:source.path,configuration:source.configuration,sourceSha256:source.sha256,parameters:{},suppression:{},transform:previous?.transform??[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1],visible:previous?.visible??true,structureSupportBinding:{version:'original-structure-support/1',hostObjectId:host.id,coordinateName:name},insulatorPlanInput:input.layoutMode?structuredClone(input):{layout:structuredClone(plan.layout),useLBracket:plan.useLBracket,bodyHeight:plan.bodyHeight,barThickness:plan.barThickness,rootCount:plan.rootCount,suppressedGroups:plan.suppressedGroups}});
  }
  for(const host of objects.filter(object=>object.baseBinding||object.frameBinding)){
    if(host.baseBinding){const binding=host.baseBinding;if(!plans.has(binding.scenario))plans.set(binding.scenario,planCabinetBases(binding.scenario,inputs[binding.scenario]));const position=plans.get(binding.scenario).plans.find(position=>position.positionName===binding.positionName);for(const placement of (position.insulators??[position.insulator]).filter(placement=>(placement.hostModelKind??placement.targetBaseKind)===host.structurePlanInput.kind))add(host,placement.hostCoordinateName??'绝缘子安装坐标系',baseInsulatorInput(placement));}
    else{const position=frames.plans.find(position=>position.positionKey===host.frameBinding.positionKey);for(let index=0;index<position.model.insulatorPositions.length;index++){const input=frameInsulatorInput(scenario,inputs[scenario].values,position.model,index);add(host,frameInsulatorCoordinate(input,index),input);}}
  }
  for(const object of result)if(object.kind==='structure-insulator-group'&&!object.structureSupportBinding)throw new DefinitionError('结构支撑缺少原安装绑定');
  return result;
}
