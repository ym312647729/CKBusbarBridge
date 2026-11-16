import {DefinitionError,solveEquations} from './expressions.mjs';
import {validateProject} from './project.mjs';
import {planCabinetBases} from './cabinet-base-planning.mjs';
import {cabinetScenePath} from './cabinet-binding.mjs';
import {equationInitialValues,resolveDimensionReference} from './equation-context.mjs';
import {planAssemblyDimensionDrives} from './equation-targets.mjs';
import {originalCabinetPartName,cabinetOpeningDimensionMap} from './cabinet-openings.mjs';
import {baseInsulatorInput,buildUniversalInsulatorDesign,buildInsulatorDraftDesign} from './insulator-design.mjs';
import {buildFrameDesignExport} from './frame-design.mjs';
import {buildWallBushingDesign,wallBushingEquationInitialValues,bindWallBushingExternalDimensions} from './wall-bushing-design.mjs';
import {buildWallBracketDesign} from './wall-bracket-design.mjs';
import {baseTemplatePaths} from './structure-sources.mjs';
import {solveStructureEquations,reconcileStructureDimensions} from './structure-equations.mjs';
import {baseInstallationDimensions,buildStructureInstallation} from './structure-installation.mjs';
import {exportCopperDesign} from './original-copper.mjs';
import {evaluateStructureInstallation} from './structure-installation-preview.mjs';
import {deriveCabinetScene} from './cabinet-scene.mjs';
import {fixedHangerDatumDefinition,hangerTemplatePath} from './hanger-installation.mjs';
import {memoizeDefinition} from './definition-memo.mjs';

export const designExportSchema='dq-bridge-design/1';
export const designPreviewPolicy={purpose:'schematic',sheetMetalCorners:'approximate',louvers:'rectangular-blocks',straightSlitStitchBelowMm:.1,thresholdComparison:'strict-less-than',nativeModelValidationRequired:false};
const canonical=name=>name.split('@').slice(0,-1).join('@');

function sceneGlobals(scenario,record){
  const values=record.values;
  if(scenario!=='face-to-wall'){const eccentricity=values['General.CabinetEccentricity'];return {'柜间距A':values['General.CabinetSpacing'],'楼高D':values['General.FloorHeight'],B:values['General.ParameterB'],'柜间偏心值':Math.abs(eccentricity),'偏移方向':eccentricity>0?1:0};}
  const rear=record.orientation==='CabinetRearToWall',eccentricity=values['General.WallCabinetEccentricity'],spec=/^\s*(\d+(?:\.\d+)?)\s*[×xX*]\s*(\d+(?:\.\d+)?)\s*$/.exec(values['General.BusbarSpec']);
  if(!spec||Number(spec[1])<=0||Number(spec[2])<=0)throw new DefinitionError('铜排规格不能生成原柜体驱动参数');
  return {[rear?'柜后距墙距离':'柜前距墙距离']:values['General.CabinetToWallDistance'],B:values['General.FloorToBeamDistance'],'下部分墙面高度':values['General.WallHoleEdgeGroundHeight'],'墙洞开口高度':values['General.WallHoleHeight'],'墙洞开口宽度':values['General.WallHoleWidth'],'搭接长度':Number(spec[1]),I:values['General.CabinetGroundHeight'],L:values['General.BuildingHeight'],'柜对墙偏心距离':Math.abs(eccentricity),'偏移方向':eccentricity<0?1:0,F1:values['General.F1'],F2:values['General.F2'],F3:values['General.F3'],J:values['General.J'],H:values['General.WallBushingTerminalToWallDistance'],D:values['General.WallEntryBusbarCenterToHoleBottomDistance']};
}

async function cabinetPlan(project,record,read){
  const scenario=project.scenario,template=await read(cabinetScenePath(scenario,record)),source=template.configurations.find(row=>row.name==='默认'),globals=sceneGlobals(scenario,record);
  if(!source)throw new DefinitionError('设计导出缺少原柜体配置');
  for(const [name,value]of Object.entries(globals))if(!Number.isFinite(value)||!source.equations.some(equation=>equation.globalVariable&&/^\s*"([^"]+)"\s*=/.exec(equation.expression)?.[1]===name))throw new DefinitionError('设计导出无法绑定原柜体全局变量：'+name);
  const context=await equationInitialValues(template,source,read),bushingPlan=scenario==='face-to-wall'?await buildWallBushingDesign(template,source,record,read):null,evaluated=solveEquations(source.equations,bushingPlan?wallBushingEquationInitialValues(context,bushingPlan):context.initialValues,globals),dimensionDrives=[];
  for(const equation of source.equations.filter(row=>!row.disabled&&row.userDisposition?.mode!=='void-by-user')){
    const target=/^\s*"([^"]+)"\s*=/.exec(equation.expression)?.[1];if(!target?.includes('@'))continue;
    const resolved=await resolveDimensionReference(template,source,target,read);dimensionDrives.push({reference:target,dimension:resolved.dimension,valueMm:evaluated[target],instancePath:resolved.instancePath,sourceEquation:equation.expression});
  }
  const groups=[];
  for(const rootComponent of source.components.filter(component=>component.path?.endsWith('.SLDASM'))){
    const child=await read(rootComponent.path),childConfiguration=child.configurations.find(row=>row.name===rootComponent.configuration);if(!childConfiguration)throw new DefinitionError('设计导出缺少原子装配配置');
    for(const body of childConfiguration.components.filter(component=>originalCabinetPartName(component.path))){
      const bodyName=originalCabinetPartName(body.path),prefix=bodyName==='A柜'?'ACabinet':bodyName==='B柜'?'BCabinet':'Cabinet',bodyTemplate=await read(body.path),configuration=bodyTemplate.configurations.find(row=>row.name===body.configuration),values=record.values,leftRight=scenario==='side-to-side'&&values['General.CabinetTopOpeningMode']==='LeftRight',holes=[];
      for(const [side,isFront]of [['FrontHole',true],['BackHole',false]]){const mapping=cabinetOpeningDimensionMap(configuration,isFront,leftRight),path=prefix+'.'+side,fields={edgeDistance:scenario==='face-to-wall'?'BusbarCenterToCabinetEdgeDistance':'ToCabinetEdgeDistance',fixedSpacing:'FixedHoleSpacing',l1:'L1',l2:'L2',l3:'L3'};holes.push({group:path,enabled:values[path+'.IsEnabled'],sketch:mapping.contract.sketchName,dimensionsMm:Object.fromEntries(Object.entries(fields).map(([key,field])=>[mapping.map[key],values[path+'.'+field]])),aliases:mapping.aliases});}
      const dimensionsMm={},bodyGlobals={};
      for(const [token,field]of [['柜高度@草图1','CabinetHeight'],['柜深度@草图1','CabinetDepth'],['柜宽度@','CabinetWidth']]){
        const dimensions=[...new Set(configuration.features.flatMap(feature=>feature.dimensions??[]).filter(dimension=>dimension.name.includes(token)).map(dimension=>canonical(dimension.name)))];if(dimensions.length!==1)throw new DefinitionError('柜体导出尺寸没有唯一原名称：'+token);
        const name=dimensions[0],assignment=configuration.equations.map(equation=>/^\s*"([^"]+)"\s*=\s*"([^"]+)"\s*$/.exec(equation.expression)).filter(match=>match?.[1]===name);if(assignment.length!==1)throw new DefinitionError('柜体导出尺寸没有唯一原全局变量');
        dimensionsMm[name]=values[prefix+'.'+field];bodyGlobals[assignment[0][2]]=values[prefix+'.'+field];
      }
      groups.push({group:prefix,instancePath:[rootComponent.name,body.name],templatePath:body.path,configuration:body.configuration,globalVariables:bodyGlobals,dimensionsMm,groundHeightMm:values[scenario==='face-to-wall'?'General.CabinetGroundHeight':prefix+'.GroundHeight'],openingMode:leftRight?'left-right':'front-back',holes});
    }
  }
  if(groups.length!==(scenario==='face-to-wall'?1:2))throw new DefinitionError('设计导出没有完整原柜体业务组');
  const bushing=bushingPlan?bindWallBushingExternalDimensions(bushingPlan,dimensionDrives):null;
  const wallBracket=scenario==='face-to-wall'?await buildWallBracketDesign(template,source,dimensionDrives,read):null;
  const derived=await deriveCabinetScene(template,source.name,scenario,record,read);
  return {templatePath:template.source.path,sourceSha256:template.source.sha256,configuration:source.name,globalVariables:globals,dimensionDrives,groups,bushing,wallBracket,fixedCopper:derived.fixedCopper,centeredWidthMates:true,missingReferenceMatePolicy:'report-and-exclude'};
}

// JSON is a design handoff, independent of mesh construction, native mass
// comparison and SW execution. Recalculate every available business plan from
// authoritative inputs; do not serialize stale preview results as drive data.
export async function buildDesignExport(input,{readTemplate,createdAt=new Date().toISOString(),memo}={}){
  const project=validateProject(input),record=project.cabinetInputs?.[project.scenario];
  if(!record||typeof readTemplate!=='function')throw new DefinitionError('设计导出缺少当前场景输入或模板定义读取入口');
  const cache=new Map(),read=async path=>{if(!cache.has(path))cache.set(path,Promise.resolve(readTemplate(path)));const definition=await cache.get(path);if(definition.source?.path!==path)throw new DefinitionError('设计导出模板身份不一致');return definition;};
  const bases=planCabinetBases(project.scenario,record),cabinet=await memoizeDefinition(memo,readTemplate,'cabinet-design',[project.scenario,bases.record],()=>cabinetPlan(project,bases.record,read)),models=[];
  for(const position of bases.plans)for(const model of [position.baseModel,position.elevatedBaseModel].filter(Boolean)){
    models.push(await memoizeDefinition(memo,readTemplate,'base-design',[position,model],async()=>{
    const path=baseTemplatePaths[model.kind];if(!path)throw new DefinitionError('基座方案没有原模板映射');
    const template=await read(path),configuration=template.configurations.find(row=>row.name==='默认');if(!configuration)throw new DefinitionError('基座原模板缺少默认配置');
    const placements=(position.insulators??[position.insulator]).filter(insulator=>(insulator.hostModelKind??insulator.targetBaseKind)===model.kind),drives=await planAssemblyDimensionDrives(template,configuration.name,model.globalVariables,read),structure=await solveStructureEquations(template,configuration.name,model.globalVariables,read,{rootDimensions:baseInstallationDimensions({insulators:placements}),businessDrive:model.kind==='Base'?{kind:'Base',hasElevatedBase:position.hasElevatedBase}:null});
    const design={modelId:position.positionName+':'+model.kind,positionName:position.positionName,kind:model.kind,templatePath:path,sourceSha256:template.source.sha256,configuration:configuration.name,globalVariables:drives.globals,dimensions:reconcileStructureDimensions(structure,drives.targets),structure,installation:{rootCoordinateName:position.rootCoordinateName??position.positionName,baseCoordinateName:position.baseInstallationCoordinateName??'安装坐标系'},insulators:structuredClone((position.insulators??[position.insulator]).filter(insulator=>(insulator.hostModelKind??insulator.targetBaseKind)===model.kind))};
    for(const [index,insulator]of design.insulators.entries())insulator.design={modelId:design.modelId+':insulator:'+(index+1),...await buildUniversalInsulatorDesign(baseInsulatorInput(insulator),read)};
    return design;}));
  }
  const insulators=[];for(const plan of project.supportPlans)insulators.push(await buildInsulatorDraftDesign(plan,read));
  const frames=project.frameDesign?await buildFrameDesignExport(project.frameDesign,read,{memo,memoOwner:readTemplate}):null;
  const installation=await buildStructureInstallation({scenario:project.scenario,inputs:bases.record,cabinet,bases:{...bases,models},frames},read);
  installation.preview=await evaluateStructureInstallation(installation,{project,cabinet,bases:{...bases,models},frames,memo,memoOwner:readTemplate},read);
  const copper=project.objects.filter(object=>['free-copper','original-copper'].includes(object.kind)).map(exportCopperDesign);
  const hangerModels=new Map(),hangerPlacements=[];
  for(const object of project.objects.filter(object=>object.hangerBinding)){
    const dimensions=object.hangerDimensions,key=JSON.stringify([dimensions.width,dimensions.angleLength]);
    if(!hangerModels.has(key)){const template=fixedHangerDatumDefinition();hangerModels.set(key,{modelId:'hanger-model:'+(hangerModels.size+1),templatePath:hangerTemplatePath,sourceSha256:template.source.sha256,configuration:object.configuration,widthMm:dimensions.width,angleLengthMm:dimensions.angleLength,generation:'fixed-hanger-template'});}
    hangerPlacements.push({objectId:object.id,modelId:hangerModels.get(key).modelId,frameObjectId:object.hangerBinding.frameId,transform:structuredClone(object.transform),templateReference:structuredClone(object.hangerBinding.templateReference)});
  }
  const hangers={models:[...hangerModels.values()],placements:hangerPlacements};
  const normalizations=Object.entries(bases.record.values).filter(([path,value])=>value!==record.values[path]).map(([path,value])=>({path,inputValue:record.values[path],designValue:value}));
  const pending=[...project.originalDesign?.status!=='ready'?['原联合设计的自动母排、基架及绝缘子规划尚未完成']:[],...!frames?.plans.length?['母排基架参数组尚未定义']:[],...(project.originalDesign?.result.issues??[]).filter(row=>row.BlocksGeneration===true||row.Severity==='Error'||row.Severity==='Blocking').map(row=>row.Message),...installation.diagnostics.map(row=>row.message)];
  return {schemaVersion:designExportSchema,purpose:'solidworks-plugin-design-input',units:{length:'mm',angle:'rad',objectTransforms:'column-major-mm',nativeTemplateTransforms:'solidworks-metres'},createdAt,project:structuredClone(project),design:{workflow:{modules:['cabinet','bridge'],cabinetIncludes:['cabinet','base','raised-base','fixed-outgoing-copper'],manualPathDrawingRequired:false},scenario:project.scenario,inputs:structuredClone(bases.record),normalizations,originalJoint:structuredClone(project.originalDesign??null),cabinet,bases:{...bases,models},frames,installation,copper,routes:structuredClone(project.routes),insulatorPlans:structuredClone(project.supportPlans),insulators,hangerPlans:structuredClone(project.hangerPlans),hangers,objects:structuredClone(project.objects),copperLaps:structuredClone(project.copperLaps??[])},previewPolicy:{...designPreviewPolicy},handoff:{nativeExecution:'user-future-solidworks-plugin',nativeModelValidationRequired:false,sourceTemplatesRechecked:false,status:pending.length?'draft':'ready',pending} };
}

export function projectFromDesignFile(file){
  if(file?.schemaVersion===designExportSchema)return validateProject(file.project);
  return validateProject(file);
}
