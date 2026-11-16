import {DefinitionError} from './expressions.mjs';
import {planNativeEquationDrives} from './native-design-drives.mjs';
import {universalInsulatorDrivePlan,universalInsulatorLayout,layoutFromCopper,createInsulatorDraft} from './insulator-planning.mjs';

export const universalInsulatorTemplatePath='模板/绝缘子模板/绝缘子通用模板/绝缘子+垫片+支架（3垫块）.SLDASM';
const insulatorFolder='模板/绝缘子模板/绝缘子通用模板/';
const identity=component=>({componentId:component.id,instance:component.name,templatePath:component.path,configuration:component.configuration});
const requireOne=(rows,label)=>{if(rows.length!==1)throw new DefinitionError(label+'缺失或不唯一');return rows[0];};

// The native distance chain defines pad roles 1 → 3 → 2. Instance suffixes
// are deliberately not used: PackAndGo can renumber them in the later plugin.
function padRoles(configuration){
  const pads=configuration.components.filter(component=>component.path?.endsWith('/8UQ.584.013.SLDPRT'));
  const chain=name=>{const mate=requireOne(configuration.features.filter(feature=>feature.name===name&&feature.mateDefinition),'垫块原距离配合 '+name);return [...new Set(mate.mateDefinition.entities.map(entity=>entity.componentId))].map(id=>pads.find(component=>component.id===id)).filter(Boolean);};
  const first=chain('距离1'),middle=chain('距离2'),last=chain('距离3');
  if(first.length!==1||middle.length!==2||last.length!==2||!middle.includes(first[0]))throw new DefinitionError('垫块原距离配合链不完整');
  const third=requireOne(middle.filter(component=>component!==first[0]),'第三角色垫块');if(!last.includes(third))throw new DefinitionError('垫块距离3未接入第三角色');const second=requireOne(last.filter(component=>component!==third),'第二角色垫块');if(second===first[0])throw new DefinitionError('垫块配合链形成环');
  return new Map([[1,first[0]],[3,third],[2,second]]);
}

export async function buildUniversalInsulatorDesign(input,read){
  if(input.layoutMode){const draft=createInsulatorDraft(input),template=await read(universalInsulatorTemplatePath),design=await buildInsulatorDraftDesign({...draft,id:'original-mounted-insulator'},read);return {kind:'mounted-insulator-design',templatePath:universalInsulatorTemplatePath,sourceSha256:template.source.sha256,configuration:'默认',layout:structuredClone(draft.layout),mount:structuredClone(draft.mount),models:design.models,installation:{coordinateName:'安装坐标系',originMm:[0,-25,0],xFlipped:false,zFlipped:false},ruleSource:'original-BridgeInsulatorMountPlan'};}
  const plan=universalInsulatorDrivePlan(input.layout,input),template=await read(universalInsulatorTemplatePath),configuration=requireOne(template.configurations.filter(row=>row.name==='默认'),'通用绝缘子原配置');
  const groups=[1,2,3].map(group=>{const mate=requireOne(configuration.features.filter(feature=>feature.name==='距离'+group&&feature.mateDefinition),'绝缘子组原距离配合');const component=requireOne(configuration.components.filter(component=>component.path?.endsWith('/绝缘子垫块.SLDASM')&&mate.mateDefinition.entities.some(entity=>entity.componentId===component.id)),'绝缘子原组 '+group);return {group,...identity(component),suppressed:plan.suppressedGroups.includes(group)};});
  if(new Set(groups.map(group=>group.componentId)).size!==3)throw new DefinitionError('绝缘子原组身份重复');
  const child=await read(groups[0].templatePath),childConfiguration=requireOne(child.configurations.filter(row=>row.name===groups[0].configuration),'共享绝缘子原配置'),roles=padRoles(childConfiguration);
  const body=requireOne(childConfiguration.components.filter(component=>component.path===insulatorFolder+'绝缘子.SLDPRT'),'共享绝缘子本体');
  const bracketPath=insulatorFolder+(plan.useLBracket?'8GX.043.021.SLDPRT':'8GX.043.002.SLDPRT'),bracket=await read(bracketPath),bracketConfiguration=requireOne(bracket.configurations.filter(row=>row.name==='默认'),'原支架配置');
  const bracketStem=plan.useLBracket?'8GX.043.021':'8GX.043.002',bracketComponent=requireOne(configuration.components.filter(component=>component.name.startsWith(bracketStem+'-')),'原支架实例');
  if(!bracketConfiguration.features.some(feature=>feature.name==='草图2'&&feature.sketch))throw new DefinitionError('支架原长度草图缺失');
  const pattern=requireOne(childConfiguration.features.filter(feature=>feature.name==='排厚12阵列'&&feature.type==='LocalLPattern'),'原12厚垫块阵列'),properties=pattern.definition?.properties;
  if(properties?.D1TotalInstances!==2||properties.D2TotalInstances!==1||properties.SeedComponentArray?.length!==3||properties.SeedComponentArray.some(seed=>![...roles.values()].some(component=>component.name===seed.feature)))throw new DefinitionError('垫块原阵列与配合角色不一致');
  const sharedPath=roles.get(1).path,pad=await read(sharedPath),padConfiguration=requireOne(pad.configurations.filter(row=>row.name===roles.get(1).configuration),'原垫块配置');
  const thicknessFeatures=padConfiguration.features.filter(feature=>['SheetMetal','SMBaseFlange'].includes(feature.type)&&feature.definition?.properties.Thickness>0);if(thicknessFeatures.length!==2)throw new DefinitionError('垫块原钣金及基体厚度定义不完整');
  const stackEnabled=plan.useStackedPads&&plan.enabledPadRoles.length>0,top=await planNativeEquationDrives(template,configuration,plan.layout.variables,read),shared=await planNativeEquationDrives(child,childConfiguration,{'绝缘子高度':plan.bodyHeight,'排厚':plan.barThickness},read);
  return {kind:'universal-insulator-design',templatePath:universalInsulatorTemplatePath,sourceSha256:template.source.sha256,configuration:configuration.name,...top,layout:structuredClone(plan.layout),groups,bracket:{componentId:bracketComponent.id,instance:bracketComponent.name,templatePath:bracketPath,sourceSha256:bracket.source.sha256,configuration:bracketConfiguration.name,useLBracket:plan.useLBracket,lengthMm:plan.layout.bracketLength,lengthSketch:'草图2',lengthAxis:'sketch-y',endPositionsMm:[-plan.layout.bracketLength/2,plan.layout.bracketLength/2],folderSuppression:{'普通支架':plan.useLBracket,'L型支架':!plan.useLBracket}},shared:{templatePath:child.source.path,configuration:childConfiguration.name,...shared,body:{...identity(body),dimension:'D2@草图1',heightMm:plan.bodyHeight},pads:{templatePath:sharedPath,configuration:padConfiguration.name,thicknessMm:plan.padThickness,thicknessFeatures:thicknessFeatures.map(feature=>({featureId:feature.id,feature:feature.name,property:'Thickness',valueMm:plan.padThickness})),roles:[...roles].map(([role,component])=>({role,...identity(component),enabled:plan.enabledPadRoles.includes(role)})),stackPattern:{featureId:pattern.id,feature:pattern.name,enabled:stackEnabled,spacingMm:plan.barThickness/2,copiesPerEnabledSeed:stackEnabled?1:0,enabledSeedRoles:stackEnabled?[...plan.enabledPadRoles]:[],generatedCopyPolicy:'one-copy-per-enabled-original-seed'}}},installation:{coordinateName:'安装坐标系',originMm:[0,-25,0],xFlipped:plan.useLBracket,zFlipped:plan.useLBracket},ruleSource:'UniversalInsulatorDrivePlan / InsulatorDriver.Universal / native-distance-chain'};
}

export async function buildInsulatorDraftDesign(draft,read){
  const models=[];
  for(const variant of insulatorDraftVariants(draft))models.push({modelId:draft.id+':groups:'+variant.groups.map(group=>group.group).join('-'),phases:variant.groups.map(group=>({group:group.group,phase:group.phase,localZ:group.localZ})),design:await buildUniversalInsulatorDesign(variant.input,read)});
  return {planId:draft.id,name:draft.name,mount:structuredClone(draft.mount),models};
}

export function insulatorDraftVariants(draft){
  const heights=[...new Set(draft.groups.filter(group=>group.active).map(group=>group.drive.bodyHeight))];
  return heights.map(height=>{const groups=draft.groups.filter(group=>group.active&&group.drive.bodyHeight===height),prototype=groups[0].drive,suppressedGroups=[1,2,3].filter(group=>!groups.some(row=>row.group===group));return {groups,input:{...prototype,suppressedGroups}};});
}

export function baseInsulatorInput(placement){
  const globals=placement.globalVariables,span=placement.bracketHostSpan,bracketLength=globals?.['支架长度']??span-20,phaseSpacing=placement.phaseSpacing??globals?.['相间距'];
  const layout=placement.layout?universalInsulatorLayout(bracketLength,placement.layout.distances.map(distance=>bracketLength/2-distance)):layoutFromCopper(bracketLength,phaseSpacing,0);
  const useLBracket=placement.hostModelKind==='Base1',group=useLBracket?/绝缘子安装坐标系([123])/.exec(placement.hostCoordinateName)?.[1]:null;
  if(useLBracket&&!group)throw new DefinitionError('基座1绝缘子没有原组安装位置');
  const suppressedGroups=group==='2'?[2]:group==='3'?[1,3]:[],spec=String(placement.busbarSpec).trim().replace(/[×*X]/g,'x').split('x'),barThickness=globals?.['排厚']??Number(spec[1]);
  return {layout,useLBracket,bodyHeight:globals?.['绝缘子高度']??placement.insulatorHeight-25,barThickness,rootCount:placement.singlePhaseRootCount,suppressedGroups};
}
