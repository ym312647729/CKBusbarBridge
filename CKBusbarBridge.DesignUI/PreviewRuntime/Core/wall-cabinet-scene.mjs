import {assignCabinetRenderRoles} from './cabinet-render-roles.mjs';
import {DefinitionError,solveEquations} from './expressions.mjs';
import {buildFixedCabinetCopper,cabinetRoofConductors} from './cabinet-fixed-copper.mjs';
import {equationInitialValues,resolveDimensionReference} from './equation-context.mjs';
import {buildWallBushingDesign,wallBushingEquationInitialValues,bindWallBushingExternalDimensions} from './wall-bushing-design.mjs';
import {deriveWallBushing,wallBushingDimensionParameters} from './wall-bushing-preview.mjs';
import {buildWallBracketDesign} from './wall-bracket-design.mjs';
import {deriveWallBracket} from './wall-bracket-preview.mjs';
import {deriveCabinetBody} from './cabinet-body.mjs';
import {deriveCabinetOpenings,originalCabinetPartName} from './cabinet-openings.mjs';
import {deriveWallScenePart} from './wall-scene-parts.mjs';
import {mapDrivenReferencePlane} from './extruded-topology.mjs';
import {solveAssemblyTranslations} from './assembly-translations.mjs';
import {swMatrix,inverseRigidMatrix,multiplyMatrix,transformPoint} from './geometry.mjs';
import {cabinetScenePath} from './cabinet-binding.mjs';
import {driveCabinetMountCoordinates} from './cabinet-mount-coordinates.mjs';
import {cabinetOffsetDimension,cabinetSpacingDimension} from './cabinet-offset-dimensions.mjs';

const canonical=name=>name.split('@').slice(0,-1).join('@');
const shifted=(transform,delta)=>{const updated=[...transform];for(let axis=0;axis<3;axis++)updated[9+axis]+=delta[axis]/1000;return updated;};
const directionTransform=(vector,matrix)=>[0,1,2].map(axis=>matrix[axis]*vector[0]+matrix[4+axis]*vector[1]+matrix[8+axis]*vector[2]);

export async function deriveWallCabinetScene(sourceTemplate,configurationName,record,readSource){
  if(cabinetScenePath('face-to-wall',record)!==sourceTemplate.source.path||record.status!=='parameters-only')throw new DefinitionError('面对墙原场景与当前朝向不一致');
  const values=record.values,source=sourceTemplate.configurations.find(configuration=>configuration.name===configurationName);
  if(!source?.components.length)throw new DefinitionError('面对墙原总装定义缺失');
  const definitions=new Map(),drivenParts=new Map(),evidence=[],diagnostics=[];
  const read=async path=>{if(!definitions.has(path))definitions.set(path,await readSource(path));return definitions.get(path);};
  const rear=record.orientation==='CabinetRearToWall',eccentricity=values['General.WallCabinetEccentricity'],spec=/^\s*(\d+(?:\.\d+)?)\s*[×xX*]\s*(\d+(?:\.\d+)?)\s*$/.exec(values['General.BusbarSpec']);
  if(!spec||Number(spec[1])<=0||Number(spec[2])<=0)throw new DefinitionError('面对墙原铜排规格无效');
  const globals={
    [rear?'柜后距墙距离':'柜前距墙距离']:values['General.CabinetToWallDistance'],B:values['General.FloorToBeamDistance'],
    '下部分墙面高度':values['General.WallHoleEdgeGroundHeight'],'墙洞开口高度':values['General.WallHoleHeight'],'墙洞开口宽度':values['General.WallHoleWidth'],'搭接长度':Number(spec[1]),
    I:values['General.CabinetGroundHeight'],L:values['General.BuildingHeight'],'柜对墙偏心距离':Math.abs(eccentricity),'偏移方向':eccentricity<0?1:0,
    F1:values['General.F1'],F2:values['General.F2'],F3:values['General.F3'],J:values['General.J'],H:values['General.WallBushingTerminalToWallDistance'],D:values['General.WallEntryBusbarCenterToHoleBottomDistance']
  };
  if(Object.values(globals).some(value=>!Number.isFinite(value)))throw new DefinitionError('面对墙原整体参数不是有限数');
  for(const name of Object.keys(globals))if(source.equations.filter(equation=>equation.globalVariable&&/^\s*"([^"]+)"\s*=/.exec(equation.expression)?.[1]===name).length!==1)throw new DefinitionError('面对墙原全局变量缺失：'+name);
  const initial=await equationInitialValues(sourceTemplate,source,read),bushingPlan=await buildWallBushingDesign(sourceTemplate,source,record,read),solved=solveEquations(source.equations,wallBushingEquationInitialValues(initial,bushingPlan),globals),rootDimensions=[];
  for(const equation of source.equations.filter(equation=>!equation.disabled&&!equation.globalVariable)){
    const reference=/^\s*"([^"]+)"\s*=/.exec(equation.expression)?.[1];if(!reference?.includes('@'))continue;const resolved=await resolveDimensionReference(sourceTemplate,source,reference,read);rootDimensions.push({reference,dimension:resolved.dimension,valueMm:solved[reference],instancePath:resolved.instancePath,sourceEquation:equation.expression});
  }
  bindWallBushingExternalDimensions(bushingPlan,rootDimensions);
  if(rear&&!source.equations.some(equation=>/^\s*"D1@距离9"\s*=/.test(equation.expression)))diagnostics.push({feature:'距离9',message:'原柜后向墙横梁离地配合未关联 B，按原固定距离保留；B 仍按原方程驱动梁截面高度'});
  for(const [name,contract]of Object.entries({'墙':{'D2@草图1':'D2@草图1@墙<1>.Part','开孔高度@草图2':'开孔高度@草图2@墙<1>.Part','开孔宽度@草图2':'开孔宽度@草图2@墙<1>.Part','D1@草图2':'D1@草图2@墙<1>.Part'},'梁':{'D2@草图1':'D2@草图1@梁<1>.Part'},'穿墙套管安装板':{'安装板长@草图1':'安装板长@草图1@穿墙套管安装板<1>.Part','安装板宽@草图1':'安装板宽@草图1@穿墙套管安装板<1>.Part'}})){
    const components=source.components.filter(component=>component.name===name+'-1');if(components.length!==1)throw new DefinitionError('面对墙业务零件实例不唯一：'+name);
    const component=components[0],original=await read(component.path),driven=deriveWallScenePart(original,component.configuration,Object.fromEntries(Object.entries(contract).map(([dimension,qualified])=>[dimension,solved[qualified]])));
    definitions.set(component.path,driven.template);drivenParts.set(component.id,{...driven,originalConfiguration:original.configurations.find(configuration=>configuration.name===component.configuration)});evidence.push({component:component.name,drive:driven.evidence});
  }
  const childComponents=source.components.filter(component=>component.name==='柜子装配体-1');if(childComponents.length!==1)throw new DefinitionError('面对墙缺少唯一柜体子装配体');
  const childComponent=childComponents[0],childTemplate=await read(childComponent.path),childSource=childTemplate.configurations.find(configuration=>configuration.name===childComponent.configuration),bodyComponents=childSource.components.filter(component=>originalCabinetPartName(component.path)==='柜');
  if(bodyComponents.length!==1)throw new DefinitionError('面对墙子装配体缺少唯一原柜体');
  const bodyComponent=bodyComponents[0],bodyTemplate=await read(bodyComponent.path),body=deriveCabinetBody(bodyTemplate,bodyComponent.configuration,{height:values['Cabinet.CabinetHeight'],depth:values['Cabinet.CabinetDepth'],width:values['Cabinet.CabinetWidth']}),bodySource=bodyTemplate.configurations.find(configuration=>configuration.name===bodyComponent.configuration);
  const hole=side=>Object.fromEntries(Object.entries({enabled:'IsEnabled',edgeDistance:'BusbarCenterToCabinetEdgeDistance',fixedSpacing:'FixedHoleSpacing',l1:'L1',l2:'L2',l3:'L3'}).map(([name,field])=>[name,values['Cabinet.'+side+'.'+field]]));
  const opened=deriveCabinetOpenings(body.template,bodyComponent.configuration,{mode:'front-back',front:hole('FrontHole'),back:hole('BackHole')});definitions.set(bodyComponent.path,opened.template);
  const bodyDelta=entity=>mapDrivenReferencePlane(entity.reference,bodySource,body.topologies).delta.map(value=>value*1000);
  const bodyMates=childSource.features.filter(feature=>!feature.suppressed&&feature.mateDefinition&&feature.mateDefinition.entities.some(entity=>entity.component===bodyComponent.name)&&feature.mateDefinition.entities.every(entity=>entity.reference&&(entity.component===bodyComponent.name||entity.componentId===-1)));
  const childInstallation=await solveAssemblyTranslations({...childSource,components:[bodyComponent],features:bodyMates},entity=>directionTransform(bodyDelta(entity),swMatrix(bodyComponent.transform)),{centeredWidthMates:bodyMates.filter(feature=>feature.mateDefinition.type===11).map(feature=>feature.name)});
  const updatedBodyTransform=shifted(bodyComponent.transform,childInstallation.translations[0].delta),child=structuredClone(childTemplate),childConfiguration=child.configurations.find(configuration=>configuration.name===childComponent.configuration);delete childConfiguration.validation;
  childConfiguration.components.find(component=>component.id===bodyComponent.id).transform=updatedBodyTransform;
  for(const component of childConfiguration.components.filter(component=>component.id!==bodyComponent.id)){component.representation={mode:'deferred',reason:'柜内伸出铜排的自由绘制业务尚未生成'};diagnostics.push({component:component.name,message:component.representation.reason});}
  childConfiguration.features=childConfiguration.features.filter(feature=>feature.type!=='CoordSys');childConfiguration.offlineAssemblySolution=childInstallation;definitions.set(childComponent.path,child);
  evidence.push({component:childComponent.name,bodyDrive:body.evidence,holes:opened.evidence,installation:childInstallation});
  const bushingPreviews=new Map();for(const model of bushingPlan.models){const originalBushing=await read(model.templatePath),derived=deriveWallBushing(originalBushing,model.configuration,model);bushingPreviews.set(model.modelId,derived);definitions.set(model.templatePath,derived.template);for(const placement of bushingPlan.placements.filter(placement=>placement.modelId===model.modelId))drivenParts.set(placement.componentId,{originalConfiguration:originalBushing.configurations.find(configuration=>configuration.name===model.configuration),topologies:derived.topologies});evidence.push({kind:'wall-bushing',modelId:model.modelId,dimensions:model.dimensions,externalDimensions:model.externalDimensions,evidence:derived.evidence});}
  const bracketPlan=await buildWallBracketDesign(sourceTemplate,source,rootDimensions,read),bracket=await deriveWallBracket(await read(bracketPlan.templatePath),bracketPlan,read),bracketComponent=source.components.find(component=>component.name===bracketPlan.rootInstance);for(const [path,definition]of bracket.definitions)definitions.set(path,definition);drivenParts.set(bracketComponent.id,{referenceDisplacement:bracket.referenceDisplacement});evidence.push({kind:'wall-bracket',models:bracketPlan.models,evidence:bracket.evidence});
  const known=source.components;
  if(known.length!==10)throw new DefinitionError('面对墙柜体、套管、支架与环境子系统实例数变化');
  const mates=source.features.filter(feature=>!feature.suppressed&&feature.mateDefinition),valid=mates.filter(feature=>feature.mateDefinition.entities.every(entity=>entity.reference)),dimensionValues={};
  for(const feature of valid.filter(feature=>feature.mateDefinition.type===5))for(const dimension of feature.dimensions.filter(dimension=>dimension.dimensionType===0)){const name=canonical(dimension.name);if(Object.hasOwn(solved,name))dimensionValues[name]=solved[name];}
  for(const feature of mates.filter(feature=>feature.mateDefinition.entities.some(entity=>!entity.reference)))diagnostics.push({assembly:sourceTemplate.source.path,feature:feature.name,message:'原配合缺失引用，不参与当前定位'});
  const geometryDisplacement=entity=>{
    const chain=entity.component.split('/'),component=known.find(component=>component.name===chain[0]);if(!component)throw new DefinitionError('面对墙有效配合引用了未求解组件');
    if(component.id===childComponent.id){
      if(chain.length===1)return mapDrivenReferencePlane(entity.reference,childSource,[]).delta.map(value=>value*1000);
      if(chain.length!==2||chain[1]!==bodyComponent.name)throw new DefinitionError('面对墙柜体实例链不一致');
      const originalPlacement=multiplyMatrix(swMatrix(component.transform),swMatrix(bodyComponent.transform)),updatedPlacement=multiplyMatrix(swMatrix(component.transform),swMatrix(updatedBodyTransform)),point=entity.parameters.slice(0,3).map(value=>value*1000),local=transformPoint(point,inverseRigidMatrix(originalPlacement)),delta=bodyDelta(entity),updated=transformPoint(local.map((value,index)=>value+delta[index]),updatedPlacement);
      return updated.map((value,index)=>value-point[index]);
    }
    const driven=drivenParts.get(component.id);if(!driven)return [0,0,0];
    if(driven.referenceDisplacement)return directionTransform(driven.referenceDisplacement(entity),swMatrix(component.transform));
    if(chain.length!==1)throw new DefinitionError('面对墙环境配合不是原零件实例');
    return directionTransform(mapDrivenReferencePlane(entity.reference,driven.originalConfiguration,driven.topologies).delta.map(value=>value*1000),swMatrix(component.transform));
  };
  const installation=await solveAssemblyTranslations({...source,components:known,features:valid},geometryDisplacement,{dimensionValues,centeredWidthMates:valid.filter(feature=>feature.mateDefinition.type===11).map(feature=>feature.name)});
  const template=structuredClone(sourceTemplate),configuration=template.configurations.find(configuration=>configuration.name===configurationName);delete configuration.validation;assignCabinetRenderRoles(configuration,'face-to-wall');
  for(const component of configuration.components)component.transform=shifted(component.transform,installation.translations.find(row=>row.componentId===component.id).delta);
  for(const equation of configuration.equations){const name=/^\s*"([^"]+)"\s*=/.exec(equation.expression)?.[1];if(equation.globalVariable&&Object.hasOwn(globals,name))equation.expression='"'+name+'" = '+globals[name];}
  for(const feature of configuration.features)for(const dimension of feature.dimensions??[])if(Object.hasOwn(dimensionValues,canonical(dimension.name)))dimension.value=dimensionValues[canonical(dimension.name)]/1000;
  const mountCoordinates=driveCabinetMountCoordinates(source,configuration,[{rootComponent:childComponent,originalBodyTransform:bodyComponent.transform,updatedBodyTransform:updatedBodyTransform,original:bodySource,derived:opened.template.configurations.find(row=>row.name===bodyComponent.configuration)}]);configuration.offlineAssemblySolution=installation;definitions.set(sourceTemplate.source.path,template);evidence.push({kind:'cabinet-mount-coordinates',...mountCoordinates});
  const cabinetComponentChain=[childComponent.name,bodyComponent.name],cabinetComponentChains=new Map([[childComponent.id,cabinetComponentChain]]);
  const placement=multiplyMatrix(swMatrix(configuration.components.find(component=>component.id===childComponent.id).transform),swMatrix(updatedBodyTransform)),parameters=[
    await cabinetSpacingDimension({source,scenario:'face-to-wall',wallRear:rear,value:values['General.CabinetToWallDistance'],installation,geometryDisplacement,cabinetComponentChains}),
    await cabinetOffsetDimension({source,scenario:'face-to-wall',value:eccentricity,globals,initialValues:wallBushingEquationInitialValues(initial,bushingPlan),solved,installation,geometryDisplacement,cabinetComponentChains})
  ];
  for(const [suffix,axis,label]of [['CabinetDepth',0,'柜深'],['CabinetHeight',1,'柜高'],['CabinetWidth',2,'柜宽']]){const name='Cabinet.'+suffix,value=values[name],edge=body.topologies[0].edges.map(pair=>pair.target).find(edge=>Math.abs(Math.abs(edge.end[axis]-edge.start[axis])*1000-value)<1e-5&&[0,1,2].filter(index=>index!==axis).every(index=>Math.abs(edge.end[index]-edge.start[index])<1e-8));if(!edge)throw new DefinitionError('面对墙柜体缺少原轮廓尺寸见证边');parameters.push({name,label,value,editable:true,annotationComponentChain:cabinetComponentChain,annotationEdges:[[transformPoint(edge.start.map(value=>value*1000),placement),transformPoint(edge.end.map(value=>value*1000),placement)]],source:'native-profile-incidence-bound-to-original-ui-field'});}
  const bushingPlacement=bushingPlan.placements[0],bushingModel=bushingPlan.models.find(model=>model.modelId===bushingPlacement.modelId),installedBushing=configuration.components.find(component=>component.id===bushingPlacement.componentId);
  parameters.push(...wallBushingDimensionParameters(bushingPreviews.get(bushingModel.modelId),bushingModel,swMatrix(installedBushing.transform))
    .map(parameter=>({...parameter,annotationComponentChain:[installedBushing.name]})));
  const wallComponent=configuration.components.find(component=>component.name==='墙-1'),wallTemplate=await read(wallComponent.path),wallProfile=wallTemplate.configurations.find(row=>row.name===wallComponent.configuration).features.find(feature=>feature.name==='草图2'),wallPlacement=multiplyMatrix(swMatrix(wallComponent.transform),inverseRigidMatrix(swMatrix(wallProfile.sketch.modelToSketch)));
  for(const [name,label]of [['General.WallHoleWidth','墙洞开口宽度'],['General.WallHoleHeight','墙洞开口高度']]){const value=values[name],edges=wallProfile.sketch.segments.filter(segment=>!segment.construction&&segment.type===0&&Math.abs(Math.hypot(...segment.geometry.end.map((coordinate,axis)=>coordinate-segment.geometry.start[axis]))*1000-value)<1e-5).map(segment=>[segment.geometry.start,segment.geometry.end].map(point=>transformPoint(point.map(coordinate=>coordinate*1000),wallPlacement)));if(!edges.length)throw new DefinitionError('墙洞业务尺寸没有原孔轮廓边');parameters.push({name,label,value,editable:true,annotationComponentChain:[wallComponent.name],annotationEdges:edges,source:'native-wall-opening-edge-bound-to-original-ui-group'});}
  diagnostics.push({message:'面对墙基座、自由铜排及接口属于后续业务组，尚未生成'});
  const fixedCopper=values['General.ExposedBusbarPhaseSpacing']?await buildFixedCabinetCopper({scenario:'face-to-wall',record,children:[{childSource,bodyComponent,body,prefix:'Cabinet',component:childComponent}],rootConfiguration:configuration,read}):null;
  const roofConductors=new Map([[bodyComponent.path,cabinetRoofConductors(fixedCopper,childComponent.name,placement)]]);
  return {template,loadTemplate:read,parameters,fixedCopper,roofConductors,evidence:[...evidence,{rootInstallation:installation,globals,dimensionValues}],diagnostics,source:'original-wall-cabinet-profiles-and-captured-mates',nativeGeometryEquivalenceVerified:false};
}
