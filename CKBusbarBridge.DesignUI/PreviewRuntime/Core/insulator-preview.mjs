import {DefinitionError} from './expressions.mjs';
import {buildUniversalInsulatorDesign,universalInsulatorTemplatePath,insulatorDraftVariants} from './insulator-design.mjs';
import {createInsulatorDraft} from './insulator-planning.mjs';
import {evaluatePreview} from './preview-quality.mjs';
import {swMatrix,inverseRigidMatrix,transformPoint,multiplyMatrix} from './geometry.mjs';
import {solveAssemblyTranslations} from './assembly-translations.mjs';

const toNative=matrix=>[matrix[0],matrix[1],matrix[2],matrix[4],matrix[5],matrix[6],matrix[8],matrix[9],matrix[10],...matrix.slice(12,15).map(value=>value/1000),1,0,0,0];
const shifted=(matrix,delta)=>{const updated=[...matrix];for(let axis=0;axis<3;axis++)updated[12+axis]+=delta[axis];return updated;};
const mm=point=>point.map(value=>value*1000),metres=point=>point.map(value=>value/1000);
const one=(rows,label)=>{if(rows.length!==1)throw new DefinitionError(label+'未唯一对应');return rows[0];};

export function deriveInsulatorBracket(source,configurationName,recipe,root){
  if(source.source.path!==recipe.bracket.templatePath)throw new DefinitionError('绝缘子支架来源不符');
  const original=one(source.configurations.filter(configuration=>configuration.name===configurationName),'原支架配置'),profile=one(original.features.filter(feature=>feature.name==='草图2'&&feature.sketch),'原支架长度轮廓'),reference=one(original.features.filter(feature=>feature.name==='草图20'&&feature.sketch),'原支架长度参考线');
  const line=one(reference.sketch.segments.filter(segment=>!segment.construction&&segment.type===0),'原支架长度参考边'),ends=[line.geometry.start[1]*1000,line.geometry.end[1]*1000].sort((a,b)=>a-b),sourceLength=ends[1]-ends[0];
  if(Math.abs(ends[0]+ends[1])>1e-5||sourceLength<=0||profile.sketch.segments.some(segment=>segment.type!==0))throw new DefinitionError('支架长度未对应原居中直线轮廓');
  const delta=(recipe.bracket.lengthMm-sourceLength)/2,mapModel=point=>[point[0],point[1]+(Math.abs(point[1])<1e-6?0:Math.sign(point[1])*delta),point[2]];
  const top=root.configurations.find(configuration=>configuration.name===recipe.configuration),bracketComponent=one(top.components.filter(component=>component.id===recipe.bracket.componentId),'原支架实例'),placement=swMatrix(bracketComponent.transform),holeSketch=one(original.features.filter(feature=>feature.name==='草图7'&&feature.sketch),'原三组支架孔草图'),holeDeltas=new Map();
  // Each native slot is identified by its captured contour and the two equal
  // semicircle centers. Its center must coincide with one named group mate.
  for(const contour of holeSketch.sketch.contours.filter(contour=>contour.closed)){
    const segments=holeSketch.sketch.segments.filter(segment=>!segment.construction&&contour.segments.includes(segment.id)),arcs=segments.filter(segment=>segment.type===1);
    if(arcs.length!==2||Math.abs(arcs[0].geometry.radius-arcs[1].geometry.radius)>1e-9)throw new DefinitionError('原支架孔没有完整的双圆弧腰孔');
    const center=arcs[0].geometry.center.map((value,axis)=>(value+arcs[1].geometry.center[axis])*500),modelCenter=transformPoint(center,inverseRigidMatrix(swMatrix(holeSketch.sketch.modelToSketch))),assemblyCenter=transformPoint(modelCenter,placement);
    const group=one(recipe.groups.filter(group=>{const component=top.components.find(component=>component.id===group.componentId);return component&&Math.abs(component.transform[11]*1000-assemblyCenter[2])<1e-5;}),'腰孔原组配合位置'),movement=recipe.layout.groupLocalZ[group.group-1]-assemblyCenter[2];
    const localDelta=transformPoint([0,0,movement],inverseRigidMatrix(shifted(placement,placement.slice(12,15).map(value=>-value))));
    const deltaSketch=transformPoint(localDelta,shifted(swMatrix(holeSketch.sketch.modelToSketch),swMatrix(holeSketch.sketch.modelToSketch).slice(12,15).map(value=>-value)));
    for(const segment of segments)holeDeltas.set(segment.id,deltaSketch);
  }
  if(holeDeltas.size!==holeSketch.sketch.segments.filter(segment=>!segment.construction).length)throw new DefinitionError('支架原孔轮廓没有完整分组');
  function mapReferences(value,key=''){
    if(Array.isArray(value)){
      if(['start','end','center','point','coordinates'].includes(key)&&value.length===3&&value.every(Number.isFinite))return metres(mapModel(mm(value)));
      if(['lineParameters','planeParameters'].includes(key)&&value.length===6){const offset=key==='lineParameters'?0:3,updated=[...value];updated.splice(offset,3,...metres(mapModel(mm(value.slice(offset,offset+3)))));return updated;}
      if(['referencePlaneTransform','transform'].includes(key)&&value.length===16&&value.every(Number.isFinite)){const matrix=swMatrix(value),origin=mapModel(matrix.slice(12,15));return toNative(shifted(matrix,origin.map((value,axis)=>value-matrix[12+axis])));}
      return value.map(item=>mapReferences(item));
    }
    if(value&&typeof value==='object')return Object.fromEntries(Object.entries(value).map(([name,item])=>[name,mapReferences(item,name)]));
    return value;
  }
  const template=structuredClone(source),configuration=template.configurations.find(row=>row.name===configurationName);delete configuration.validation;
  for(const feature of configuration.features){
    const sourceFeature=original.features.find(row=>row.id===feature.id);if(feature.definition)feature.definition=mapReferences(feature.definition);
    if(!sourceFeature.sketch)continue;
    const sourceMatrix=swMatrix(sourceFeature.sketch.modelToSketch),sourceToModel=inverseRigidMatrix(sourceMatrix),oldOrigin=sourceToModel.slice(12,15),newOrigin=mapModel(oldOrigin),newToModel=shifted(sourceToModel,newOrigin.map((value,axis)=>value-oldOrigin[axis])),newMatrix=inverseRigidMatrix(newToModel);
    const mapLocal=point=>metres(transformPoint(mapModel(transformPoint(mm(point),sourceToModel)),newMatrix));
    for(const segment of feature.sketch.segments){if(feature.id===holeSketch.id){const movement=holeDeltas.get(segment.id);if(movement)for(const key of ['start','end','center'])if(segment.geometry[key])segment.geometry[key]=segment.geometry[key].map((value,axis)=>value+movement[axis]/1000);}else{if(segment.type!==0&&!segment.construction)throw new DefinitionError('支架长度驱动遇到未分组曲线');for(const key of ['start','end','center'])if(segment.geometry[key])segment.geometry[key]=mapLocal(segment.geometry[key]);}}
    if(feature.id!==holeSketch.id)for(const point of feature.sketch.points??[])point.coordinates=mapLocal(point.coordinates);
    feature.sketch.modelToSketch=toNative(newMatrix);
  }
  return {template,sourceLengthMm:sourceLength,lengthMm:recipe.bracket.lengthMm,holeGroups:recipe.layout.groupLocalZ};
}

export async function deriveInsulatorAssembly(input,read){
  const recipe=await buildUniversalInsulatorDesign(input,read),root=await read(universalInsulatorTemplatePath),sourceBracket=await read(recipe.bracket.templatePath),bracket=deriveInsulatorBracket(sourceBracket,recipe.bracket.configuration,recipe,root),sourceBody=await read(recipe.shared.body.templatePath),sourcePad=await read(recipe.shared.pads.templatePath),sourceChild=await read(recipe.shared.templatePath),childConfiguration=structuredClone(sourceChild.configurations.find(configuration=>configuration.name===recipe.shared.configuration));
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
  return {recipe,root,bracket,sourceBody,sourcePad,childConfiguration,heightRatio,thicknessRatio,solution};
}

export async function evaluateInsulatorDesign(kernel,input,read,settings={}){
  if(input.layoutMode)return evaluateInsulatorDraftPreview(kernel,input,read,settings);
  const started=performance.now(),{recipe,root,bracket,sourceBody,sourcePad,childConfiguration,heightRatio,thicknessRatio,solution}=await deriveInsulatorAssembly(input,read);
  const parts=[],solids=[],solidSources=[];
  try{
    for(const [template,configuration]of [[bracket.template,recipe.bracket.configuration],[sourceBody,recipe.shared.body.configuration],[sourcePad,recipe.shared.pads.configuration]])parts.push(await evaluatePreview(kernel,template,configuration,{},settings,read));
    const top=root.configurations.find(configuration=>configuration.name===recipe.configuration),bracketPlacement=swMatrix(top.components.find(component=>component.id===recipe.bracket.componentId).transform);
    const bracketComponent=top.components.find(component=>component.id===recipe.bracket.componentId);
    for(const solid of parts[0].solids){solidSources.push({path:bracket.template.source.path,componentChain:[bracketComponent.name],bodyIndex:solidSources.length,previewMaterial:'bracket'});solids.push(solid.transform(bracketPlacement));}
    for(const group of recipe.groups.filter(group=>!group.suppressed)){
      const sourceGroup=top.components.find(component=>component.id===group.componentId),groupPlacement=swMatrix(sourceGroup.transform);groupPlacement[14]=recipe.layout.groupLocalZ[group.group-1];
      for(const component of childConfiguration.components){const isBody=component.id===recipe.shared.body.componentId,role=recipe.shared.pads.roles.find(role=>role.componentId===component.id);if(!isBody&&!role?.enabled)continue;
        const motion=solution.translations.find(row=>row.componentId===component.id),componentPlacement=shifted(swMatrix(component.transform),motion?.delta??[0,0,0]),copies=isBody?0:recipe.shared.pads.stackPattern.copiesPerEnabledSeed;
        for(let copy=0;copy<=copies;copy++){const placement=multiplyMatrix(groupPlacement,shifted(componentPlacement,[0,copy*recipe.shared.pads.stackPattern.spacingMm,0]));for(const sourceSolid of parts[isBody?1:2].solids){const scaled=sourceSolid.scale([1,isBody?heightRatio:thicknessRatio,1]);try{solids.push(scaled.transform(placement));solidSources.push({path:(isBody?sourceBody:sourcePad).source.path,componentChain:[sourceGroup.name,component.name],bodyIndex:solidSources.length,patternCopy:copy,previewMaterial:isBody?'insulator':'pad'});}finally{scaled.delete();}}}
      }
    }
    const triangleCount=solids.reduce((sum,solid)=>sum+solid.numTri(),0),overBudget=triangleCount>(settings.objectTriangleBudget??80000);
    return {solids,solidSources,recipe,parameters:[],parameterMs:0,geometryTimings:{cutFeatureMs:parts.reduce((sum,result)=>sum+(result.geometryTimings?.cutFeatureMs??0),0),otherFeatureMs:performance.now()-started},schematic:[{kind:'driven-insulator',policyId:'user-20261103-schematic-design',method:'native-bracket-slots-and-axial-body-height',nativeModelValidationRequired:false}],deferred:[],preview:{triangleCount,complexFeatureCount:parts.reduce((sum,result)=>sum+result.preview.complexFeatureCount,0)},previewDisplayMode:overBudget?'outline':'solid',qualityWarning:[...new Set([...parts.map(result=>result.qualityWarning),overBudget?'绝缘子组超出面片预算，以轮廓显示':null].filter(Boolean))].join('；')||null,evidence:{bracket:{sourceLengthMm:bracket.sourceLengthMm,lengthMm:bracket.lengthMm,holeGroups:bracket.holeGroups},assembly:solution}};
  }catch(error){solids.forEach(solid=>solid.delete());throw error;}finally{parts.forEach(result=>result.solids.forEach(solid=>solid.delete()));}
}

export async function evaluateInsulatorDraftPreview(kernel,input,read,settings={}){
  const draft=createInsulatorDraft(input),variants=insulatorDraftVariants(draft),solids=[],solidSources=[],recipes=[],evidence=[];let preview,qualityWarning,geometryTimings={cutFeatureMs:0,otherFeatureMs:0};
  try{
    for(const [index,variant]of variants.entries()){
      const result=await evaluateInsulatorDesign(kernel,variant.input,read,settings);if(index){result.solids.shift().delete();result.solidSources.shift();}solidSources.push(...result.solidSources.map((source,sourceIndex)=>({...source,bodyIndex:solidSources.length+sourceIndex})));solids.push(...result.solids);recipes.push(result.recipe);evidence.push(result.evidence);preview=result.preview;qualityWarning??=result.qualityWarning;for(const key of Object.keys(geometryTimings))geometryTimings[key]+=result.geometryTimings[key];
    }
    const parameters=[{name:'bracketLength',label:'支架长度',value:draft.layout.bracketLength,editable:true,annotationEdges:[[[50,-25,-draft.layout.bracketLength/2],[50,-25,draft.layout.bracketLength/2]]]}];
    for(const group of draft.groups.filter(group=>group.active))parameters.push({name:'bodyHeight'+group.group,label:group.phase+'相本体高度',value:group.drive.bodyHeight,editable:true,annotationEdges:[[[50,0,group.localZ],[50,group.drive.bodyHeight,group.localZ]]]});
    const triangleCount=solids.reduce((sum,solid)=>sum+solid.numTri(),0),overBudget=triangleCount>(settings.objectTriangleBudget??80000);
    return {solids,solidSources,parameters,parameterMs:0,geometryTimings,schematic:[{kind:'driven-insulator',policyId:'user-20261103-schematic-design',method:'native-bracket-slots-and-axial-body-height',nativeModelValidationRequired:false}],deferred:[],preview:{...preview,triangleCount},previewDisplayMode:overBudget?'outline':'solid',qualityWarning:overBudget?'绝缘子组超出面片预算，以轮廓显示':qualityWarning,recipes,evidence};
  }catch(error){solids.forEach(solid=>solid.delete());throw error;}
}
