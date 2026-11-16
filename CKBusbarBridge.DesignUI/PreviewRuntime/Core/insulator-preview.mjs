import {DefinitionError} from './expressions.mjs';
import {buildUniversalInsulatorDesign,universalInsulatorTemplatePath,insulatorDraftVariants} from './insulator-design.mjs';
import {createInsulatorDraft} from './insulator-planning.mjs';
import {evaluatePreview} from './preview-quality.mjs';
import {swMatrix,inverseRigidMatrix,transformPoint,multiplyMatrix} from './geometry.mjs';
import {solveAssemblyTranslations} from './assembly-translations.mjs';
import {ribbedInsulator,supportRailTemplate} from './fixed-structure-templates.mjs';

const shifted=(matrix,delta)=>{const updated=[...matrix];for(let axis=0;axis<3;axis++)updated[12+axis]+=delta[axis];return updated;};
const one=(rows,label)=>{if(rows.length!==1)throw new DefinitionError(label+'未唯一对应');return rows[0];};

export async function deriveInsulatorAssembly(input,read){
  const recipe=await buildUniversalInsulatorDesign(input,read),root=await read(universalInsulatorTemplatePath),sourceBody=await read(recipe.shared.body.templatePath),sourcePad=await read(recipe.shared.pads.templatePath),sourceChild=await read(recipe.shared.templatePath),childConfiguration=structuredClone(sourceChild.configurations.find(configuration=>configuration.name===recipe.shared.configuration));
  const nativeBody=sourceBody.configurations.find(configuration=>configuration.name===recipe.shared.body.configuration),bodyDimensions=nativeBody.features.flatMap(feature=>feature.dimensions).filter(dimension=>dimension.name.startsWith('D2@草图1@')),sourceHeight=one([...new Set(bodyDimensions.map(dimension=>dimension.value*1000))],'原绝缘子高度'),heightRatio=recipe.shared.body.heightMm/sourceHeight;
  const padFeatures=sourcePad.configurations.find(configuration=>configuration.name===recipe.shared.pads.configuration).features.filter(feature=>feature.type==='SMBaseFlange'),sourceThickness=one(padFeatures,'原垫块基体').definition.properties.Thickness*1000,thicknessRatio=recipe.shared.pads.thicknessMm/sourceThickness;
  const originalIds=new Set([recipe.shared.body.componentId,...recipe.shared.pads.roles.map(role=>role.componentId)]);childConfiguration.components=childConfiguration.components.filter(component=>originalIds.has(component.id)).map(component=>({...component,suppression:2}));
  childConfiguration.features=childConfiguration.features.filter(feature=>!feature.mateDefinition||feature.mateDefinition.entities.every(entity=>entity.componentId===-1||originalIds.has(entity.componentId)));
  // These two source symmetry mates list the body datum first and the two pad
  // faces last. Normalize their known roles for the midpoint equation solver.
  for(const feature of childConfiguration.features.filter(feature=>feature.mateDefinition?.type===8)){
    const entities=feature.mateDefinition.entities,body=entities.filter(entity=>entity.componentId===recipe.shared.body.componentId),pad=entities.filter(entity=>entity.componentId===recipe.shared.pads.roles[0].componentId);
    if(body.length!==1||pad.length!==2)throw new DefinitionError('绝缘子对称配合未对应原本体基准面及首垫块双面');feature.mateDefinition.entities=[...pad,body[0]];
  }
  const solution=await solveAssemblyTranslations(childConfiguration,entity=>{
    const component=childConfiguration.components.find(component=>component.id===entity.componentId);if(!component)throw new DefinitionError('绝缘子配合原组件缺失');
    const matrix=swMatrix(component.transform),point=entity.parameters.slice(0,3).map(value=>value*1000),local=transformPoint(point,inverseRigidMatrix(matrix)),ratio=entity.componentId===recipe.shared.body.componentId?heightRatio:thicknessRatio,newPoint=transformPoint([local[0],local[1]*ratio,local[2]],matrix);return newPoint.map((value,axis)=>value-point[axis]);
  },{dimensionValues:Object.fromEntries(recipe.shared.dimensions.filter(dimension=>dimension.dimension.startsWith('D1@距离')).map(dimension=>[dimension.dimension,dimension.valueMm]))});
  return {recipe,root,sourceBody,sourcePad,childConfiguration,heightRatio,thicknessRatio,solution};
}

export async function evaluateInsulatorDesign(kernel,input,read,settings={}){
  if(input.layoutMode)return evaluateInsulatorDraftPreview(kernel,input,read,settings);
  const started=performance.now(),{recipe,root,sourcePad,childConfiguration,thicknessRatio,solution}=await deriveInsulatorAssembly(input,read);
  const parts=[],solids=[],solidSources=[];
  try{
    parts.push(await evaluatePreview(kernel,sourcePad,recipe.shared.pads.configuration,{},settings,read));
    const top=root.configurations.find(configuration=>configuration.name===recipe.configuration);
    const bracketComponent=top.components.find(component=>component.id===recipe.bracket.componentId);
    const halfLength=recipe.bracket.lengthMm/2,bodyHeight=recipe.shared.body.heightMm;
    const rail=supportRailTemplate(kernel,{min:[-50,-25,-halfLength],max:[50,0,halfLength]},recipe.layout,recipe.bracket.useLBracket);
    for(const solid of rail.solids){solidSources.push({path:'fixed-support',componentChain:[bracketComponent.name],bodyIndex:solidSources.length,appearanceRole:'bracket',previewMaterial:'bracket'});solids.push(solid);}
    const bodyBounds={min:[-50,0,-50],max:[50,bodyHeight,50]};
    for(const group of recipe.groups.filter(group=>!group.suppressed)){
      const sourceGroup=top.components.find(component=>component.id===group.componentId),groupPlacement=swMatrix(sourceGroup.transform);groupPlacement[14]=recipe.layout.groupLocalZ[group.group-1];
      for(const component of childConfiguration.components){const isBody=component.id===recipe.shared.body.componentId,role=recipe.shared.pads.roles.find(role=>role.componentId===component.id);if(!isBody&&!role?.enabled)continue;
        const motion=solution.translations.find(row=>row.componentId===component.id),componentPlacement=shifted(swMatrix(component.transform),motion?.delta??[0,0,0]),copies=isBody?0:recipe.shared.pads.stackPattern.copiesPerEnabledSeed;
        for(let copy=0;copy<=copies;copy++){
          const placement=multiplyMatrix(groupPlacement,shifted(componentPlacement,[0,copy*recipe.shared.pads.stackPattern.spacingMm,0]));
          const generated=isBody?[ribbedInsulator(kernel,bodyBounds,recipe.shared.body.heightMm,settings.complexArcSegments??settings.arcSegments??24)]:
            parts[0].solids.map(source=>source.scale([1,thicknessRatio,1]));
          try{for(const solid of generated){solids.push(solid.transform(placement));solidSources.push({path:isBody?'fixed-insulator':sourcePad.source.path,componentChain:[sourceGroup.name,component.name],bodyIndex:solidSources.length,patternCopy:copy,appearanceRole:isBody?'insulator':'pad',previewMaterial:isBody?'insulator':'pad'});}}
          finally{generated.forEach(solid=>solid.delete());}
        }
      }
    }
    const triangleCount=solids.reduce((sum,solid)=>sum+solid.numTri(),0),overBudget=triangleCount>(settings.objectTriangleBudget??80000);
    return {solids,solidSources,recipe,parameters:[],parameterMs:0,geometryTimings:{cutFeatureMs:parts.reduce((sum,result)=>sum+(result.geometryTimings?.cutFeatureMs??0),0),otherFeatureMs:performance.now()-started},schematic:[{kind:'driven-insulator',policyId:'user-20261103-schematic-design',method:'fixed-profile-and-original-installation-datums',nativeModelValidationRequired:false}],deferred:[],preview:{triangleCount,complexFeatureCount:parts.reduce((sum,result)=>sum+result.preview.complexFeatureCount,0)},previewDisplayMode:overBudget?'outline':'solid',qualityWarning:[...new Set([...parts.map(result=>result.qualityWarning),overBudget?'绝缘子组超出面片预算，以轮廓显示':null].filter(Boolean))].join('；')||null,evidence:{bracket:{lengthMm:recipe.bracket.lengthMm,holeGroups:recipe.layout.groupLocalZ},assembly:solution}};
  }catch(error){solids.forEach(solid=>solid.delete());throw error;}finally{parts.forEach(result=>result.solids.forEach(solid=>solid.delete()));}
}

export async function evaluateInsulatorDraftPreview(kernel,input,read,settings={}){
  const draft=createInsulatorDraft(input),variants=insulatorDraftVariants(draft),solids=[],solidSources=[],recipes=[],evidence=[];let preview,qualityWarning,geometryTimings={cutFeatureMs:0,otherFeatureMs:0};
  try{
    for(const [index,variant]of variants.entries()){
      const result=await evaluateInsulatorDesign(kernel,variant.input,read,settings);
      if(index)while(result.solidSources[0]?.previewMaterial==='bracket'){result.solids.shift().delete();result.solidSources.shift();}
      solidSources.push(...result.solidSources.map((source,sourceIndex)=>({...source,bodyIndex:solidSources.length+sourceIndex})));solids.push(...result.solids);recipes.push(result.recipe);evidence.push(result.evidence);preview=result.preview;qualityWarning??=result.qualityWarning;for(const key of Object.keys(geometryTimings))geometryTimings[key]+=result.geometryTimings[key];
    }
    const parameters=[{name:'bracketLength',label:'支架长度',value:draft.layout.bracketLength,editable:true,annotationEdges:[[[50,-25,-draft.layout.bracketLength/2],[50,-25,draft.layout.bracketLength/2]]]}];
    for(const group of draft.groups.filter(group=>group.active))parameters.push({name:'bodyHeight'+group.group,label:group.phase+'相本体高度',value:group.drive.bodyHeight,editable:true,annotationEdges:[[[50,0,group.localZ],[50,group.drive.bodyHeight,group.localZ]]]});
    const triangleCount=solids.reduce((sum,solid)=>sum+solid.numTri(),0),overBudget=triangleCount>(settings.objectTriangleBudget??80000);
    return {solids,solidSources,parameters,parameterMs:0,geometryTimings,schematic:[{kind:'driven-insulator',policyId:'user-20261103-schematic-design',method:'fixed-profile-and-original-installation-datums',nativeModelValidationRequired:false}],deferred:[],preview:{...preview,triangleCount},previewDisplayMode:overBudget?'outline':'solid',qualityWarning:overBudget?'绝缘子组超出面片预算，以轮廓显示':qualityWarning,recipes,evidence};
  }catch(error){solids.forEach(solid=>solid.delete());throw error;}
}
