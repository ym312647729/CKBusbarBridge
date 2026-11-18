import {DefinitionError,solveEquations} from './expressions.mjs';
import {stitchPreviewSlitGroups} from './preview-seams.mjs';
import {identityMatrix,patternOperations,referencePlane,multiplyMatrix} from './feature-patterns.mjs';
export {multiplyMatrix} from './feature-patterns.mjs';
import {louverOperations} from './schematic-louver.mjs';
import {chamferOperations} from './edge-chamfer.mjs';
import {resolveSketchRegion} from './selected-regions.mjs';
import {revolveFeature} from './revolved-feature.mjs';
import {unionProfileSections} from './profile-section.mjs';
import {equationInitialValues} from './equation-context.mjs';
import {edgeFlangeOperations} from './edge-flange.mjs';
import {openSheetBoundary} from './open-sheet.mjs';
import {linearSketchDrive} from './linear-sketch-drive.mjs';
import {circleSegmentsForTolerance} from './curve-quality.mjs';
import {componentState,componentMassTotals} from './component-state.mjs';
import {NativeBodyTracker} from './native-bodies.mjs';
import {planarMixedMesh,meshFaceLabels} from './planar-mesh.mjs';
const metadataTypes=new Set(['RefPlane','RefAxis','CoordSys','OriginProfileFeature','DetailCabinet','AnnotationViewFeat','CosmeticThread','WeldmentFeature','SheetMetal','MateGroup','Reference','ReferencePattern','InContextFeatHolder','CompositeCurve','3DProfileFeature','ProfileFeature','LiveSectionFolder']);
const neutralType=type=>metadataTypes.has(type)||type.endsWith('Folder');
function suppressedInTree(feature,configuration){
  const visited=new Set();
  while(feature){
    if(feature.suppressed)return true;
    if(visited.has(feature.name))throw new DefinitionError('源特征包含层级循环：'+feature.name);
    visited.add(feature.name);feature=feature.parent?configuration.features.find(candidate=>candidate.name===feature.parent):null;
  }
  return false;
}
function consumedBend(feature,configuration){
  if(!['OneBend','SketchBend'].includes(feature.type)||!feature.parent)return false;
  const owner=configuration.features.find(candidate=>candidate.name===feature.parent);
  if(feature.type==='SketchBend')return owner?.type==='SMBaseFlange';
  return owner&&['EdgeFlange','MirrorPattern','LPattern'].includes(owner.type);
}
export function activeTemplateGeometry(configuration){return configuration.features.filter(feature=>!suppressedInTree(feature,configuration)&&!neutralType(feature.type)&&!consumedBend(feature,configuration));}
function verifyUnbentFlatPattern(feature,configuration){
  const properties=feature.definition?.properties;
  const geometricFeatures=configuration.features.filter(candidate=>!suppressedInTree(candidate,configuration)&&!neutralType(candidate.type)&&candidate.type!=='FlatPattern');
  if(geometricFeatures.length!==1||geometricFeatures[0].type!=='SMBaseFlange'||properties?.excludedFacesCount!==0||properties.ExcludedFaces?.length||properties.BreakCornerType!==0)throw new DefinitionError('含折弯或边角处理的展平尚未支持：'+feature.name);
  const base=geometricFeatures[0],baseProperties=base.definition?.properties,profiles=configuration.features.filter(candidate=>candidate.sketch&&base.parents.includes(candidate.name));
  if(profiles.length!==1||!profiles[0].sketch.contours?.length||profiles[0].sketch.contours.some(contour=>!contour.closed))throw new DefinitionError('展平没有唯一闭合的平板源轮廓：'+feature.name);
  const plane=referencePlane(properties.FixedFace2),matrix=inverseRigidMatrix(swMatrix(profiles[0].sketch.modelToSketch)),normal=matrix.slice(8,11),origin=matrix.slice(12,15);
  if(Math.abs(Math.abs(normal.reduce((sum,value,index)=>sum+value*plane.normal[index],0))-1)>1e-8)throw new DefinitionError('展平固定面与原平板不平行：'+feature.name);
  const distance=normal.reduce((sum,value,index)=>sum+value*(plane.origin[index]-origin[index]),0),thickness=baseProperties.Thickness*1000;
  const lower=baseProperties.SymmetricThickness?-thickness/2:baseProperties.ReverseThickness?0:-thickness;
  if(Math.min(Math.abs(distance-lower),Math.abs(distance-lower-thickness))>1e-5)throw new DefinitionError('展平固定面不在原平板表面：'+feature.name);
}
export const length3=vector=>Math.hypot(...vector);
export const difference3=(left,right)=>left.map((value,index)=>value-right[index]);
export function swMatrix(data,scaleTranslation=1000){
  if(!Array.isArray(data)||data.length<13||!data.slice(0,13).every(Number.isFinite))throw new DefinitionError('源安装矩阵无效');
  const scale=data[12];return [data[0]*scale,data[1]*scale,data[2]*scale,0,data[3]*scale,data[4]*scale,data[5]*scale,0,data[6]*scale,data[7]*scale,data[8]*scale,0,data[9]*scaleTranslation,data[10]*scaleTranslation,data[11]*scaleTranslation,1];
}
export function inverseRigidMatrix(matrix){
  const result=[matrix[0],matrix[4],matrix[8],0,matrix[1],matrix[5],matrix[9],0,matrix[2],matrix[6],matrix[10],0,0,0,0,1];
  for(let row=0;row<3;row++)result[12+row]=-(result[row]*matrix[12]+result[4+row]*matrix[13]+result[8+row]*matrix[14]);return result;
}
export function transformPoint(point,matrix){return [0,1,2].map(row=>matrix[row]*point[0]+matrix[row+4]*point[1]+matrix[row+8]*point[2]+matrix[row+12]);}
export function sampleSegment(segment,arcSegments=24){
  const geometry=segment.geometry;
  if(segment.type===0)return [geometry.start.slice(0,2).map(value=>value*1000),geometry.end.slice(0,2).map(value=>value*1000)];
  if(segment.type!==1)throw new DefinitionError('未支持的草图曲线：'+segment.name,{segmentId:segment.id,type:segment.type});
  const radius=geometry.radius*1000,center=geometry.center.map(value=>value*1000),start=Math.atan2(geometry.start[1]-geometry.center[1],geometry.start[0]-geometry.center[0]),end=Math.atan2(geometry.end[1]-geometry.center[1],geometry.end[0]-geometry.center[0]);
  let sweep=end-start;const full=length3(difference3(geometry.end,geometry.start))<1e-10;
  const direction=geometry.rotation>=0?1:-1;
  if(full)sweep=2*Math.PI*direction;else if(direction>0&&sweep<=0)sweep+=2*Math.PI;else if(direction<0&&sweep>=0)sweep-=2*Math.PI;
  const requestedSegments=typeof arcSegments==='object'?circleSegmentsForTolerance(radius,arcSegments.chordToleranceMm,arcSegments.segments):arcSegments;
  const steps=Math.max(1,Math.ceil(Math.abs(sweep)/(2*Math.PI/requestedSegments)));
  return Array.from({length:steps+1},(_,index)=>[center[0]+radius*Math.cos(start+sweep*index/steps),center[1]+radius*Math.sin(start+sweep*index/steps)]);
}
export function sketchLoops(sketch,arcSegments=24){
  if(!sketch?.segments)throw new DefinitionError('特征草图缺失');
  const remaining=sketch.segments.filter(segment=>!segment.construction).map(segment=>({id:segment.id,points:sampleSegment(segment,arcSegments)}));
  const loops=[];
  while(remaining.length){
    const current=remaining.shift();const points=[...current.points];
    while(Math.hypot(points[0][0]-points.at(-1)[0],points[0][1]-points.at(-1)[1])>1e-5){
      const endpoint=points.at(-1);let candidateIndex=-1,reverse=false;
      for(let index=0;index<remaining.length;index++){
        const candidate=remaining[index].points;
        if(Math.hypot(endpoint[0]-candidate[0][0],endpoint[1]-candidate[0][1])<1e-5){candidateIndex=index;break;}
        if(Math.hypot(endpoint[0]-candidate.at(-1)[0],endpoint[1]-candidate.at(-1)[1])<1e-5){candidateIndex=index;reverse=true;break;}
      }
      if(candidateIndex<0)throw new DefinitionError('草图轮廓未闭合，禁止用包围盒替代',{segmentId:current.id});
      const next=remaining.splice(candidateIndex,1)[0].points;if(reverse)next.reverse();points.push(...next.slice(1));
    }
    points.pop();if(points.length<3)throw new DefinitionError('草图轮廓无效');loops.push(points);
  }
  return loops;
}
function selectedProfileGroups(sketch,properties,arcSegments,featureName){
  if(!properties.Contours?.length)return [sketchLoops(sketch,arcSegments)];
  return properties.Contours.map(contour=>{
    if(contour.kind==='source-sketch-region')return resolveSketchRegion(sketch,contour).loops.flatMap(loop=>sketchLoops({...sketch,segments:loop.segments},arcSegments));
    const selectedIds=new Set();
    if(!contour.closed||!contour.contourSegments?.length)throw new DefinitionError('拉伸的选定轮廓尚未完成原始区域求值：'+featureName);
    for(const identity of contour.contourSegments){
      if(!sketch.segments.some(segment=>segment.id===identity))throw new DefinitionError('选定轮廓段无法对应源草图：'+featureName);
      selectedIds.add(identity);
    }
    return sketchLoops({...sketch,segments:sketch.segments.filter(segment=>selectedIds.has(segment.id))},arcSegments);
  });
}
function openBaseFlange(kernel,sketch,properties,arcSegments,orientationWitnesses){
  const boundary=openSheetBoundary(sketch,properties,arcSegments,orientationWitnesses);
  let width=properties.D1EndConditionDistance*1000,offset=properties.ReverseDirection?-width:0;
  if(properties.D1EndConditionType===5)offset=-width/2;
  else if(properties.D1EndConditionType!==1)throw new DefinitionError('开口基体的宽度终止条件尚未支持：'+properties.D1EndConditionType);
  if(properties.OffsetDirections!==1)throw new DefinitionError('开口基体的双向宽度尚未支持');
  if(!(width>0))throw new DefinitionError('开口基体的宽度无效');
  const section=new kernel.CrossSection([boundary],'EvenOdd');
  try{const extruded=section.extrude(width);try{return extruded.translate([0,0,offset]);}finally{extruded.delete();}}finally{section.delete();}
}
export function listParameters(template,configurationName){
  const configuration=template.configurations.find(config=>config.name===configurationName);
  if(!configuration)throw new DefinitionError('模板配置不存在：'+configurationName);
  const values=new Map();
  for(const feature of configuration.features)for(const dimension of feature.dimensions){
    const name=dimension.name.split('@').slice(0,-1).join('@');
    if(!values.has(name))values.set(name,{name,label:name,value:dimension.value*1000,editable:false,sourceFeature:feature.name,annotation:dimension.annotation,nativeDrivingState:dimension.drivenState,nativeReadOnly:dimension.readOnly,reason:dimension.drivenState===1?'原生从动尺寸':dimension.readOnly?'原生只读尺寸':'草图约束或特征驱动尚未验证'});
  }
  for(const equation of configuration.equations){
    if(equation.disabled||equation.userDisposition?.mode==='void-by-user')continue;
    const match=/^\s*"([^"]+)"\s*=\s*([+\-\d.]+)\s*$/.exec(equation.expression);
    if(match&&!match[1].includes('@'))values.set(match[1],{name:match[1],label:match[1],value:Number(match[2]),editable:false,sourceFeature:'方程式',reason:'方程依赖几何求解尚未验证'});
  }
  // Only expose depth changes for validated blind/mid-plane extrusions. Editing a value must alter geometry.
  const activeGeometry=configuration.features.filter(feature=>!suppressedInTree(feature,configuration)&&!neutralType(feature.type)&&!consumedBend(feature,configuration));
  for(const feature of configuration.features){
    if(!['Extrusion','Boss'].includes(feature.type))continue;
    if(activeGeometry.length!==1||activeGeometry[0].id!==feature.id)continue;
    const properties=feature.definition?.properties;if(!properties||![0,6].includes(properties.forwardEndCondition))continue;
    for(const dimension of feature.dimensions){
      const name=dimension.name.split('@').slice(0,-1).join('@');
      if(name.endsWith('@'+feature.name)&&dimension.drivenState===2&&dimension.readOnly===false&&Math.abs(dimension.value-properties.forwardDepth)<1e-8&&values.has(name)){values.get(name).editable=true;values.get(name).reason=null;}
    }
  }
  if(activeGeometry.length===1&&['Extrusion','Boss','SMBaseFlange'].includes(activeGeometry[0].type)){
    const drive=linearSketchDrive(configuration,activeGeometry[0]);
    for(const dimension of drive?.dimensions??[])if(dimension.editable&&values.has(dimension.name)){values.get(dimension.name).editable=true;values.get(dimension.name).reason=null;values.get(dimension.name).driveMode='complete-linear-sketch-system';}
  }
  return [...values.values()];
}
export async function evaluateTemplate(kernel,template,configurationName,overrides={},settings={},loadTemplate,ancestry=[]){
  const authored=settings.fixedPartTemplate?.(kernel,template,configurationName,null);
  let result=authored??await evaluateTemplateCore(kernel,template,configurationName,overrides,settings,loadTemplate,ancestry);
  try{
    const replacement=authored??settings.fixedPartTemplate?.(kernel,template,configurationName,result);
    if(replacement){
      if(!authored)result.solids.forEach(solid=>solid.delete());
      result={...result,...replacement,parameters:result.parameters,parameterMs:result.parameterMs};
      if(settings.captureLocalMeshes)result.solidSources=result.solidSources.map((source,index)=>
        ({...source,transform:[...identityMatrix],localMesh:solidToMesh(result.solids[index])}));
    }
  }catch(error){result.solids.forEach(solid=>solid.delete());throw error;}
  if(settings.captureLocalMeshes&&!result.solidSources){
    try{result.solidSources=result.solids.map((solid,bodyIndex)=>({path:template.source.path,configuration:configurationName,sha256:template.source.sha256,
      bodyIndex,componentChain:[],transform:[...identityMatrix],localMesh:solidToMesh(solid),
      shapeInputSignature:JSON.stringify([template.source.sha256,configurationName,overrides,result.parameters?.map(parameter=>[parameter.name,parameter.value])])}));}
    catch(error){result.solids.forEach(solid=>solid.delete());throw error;}
  }
  return result;
}
async function evaluateTemplateCore(kernel,template,configurationName,overrides={},settings={},loadTemplate,ancestry=[]){
  const start=performance.now();
  let configuration=template.configurations.find(config=>config.name===configurationName);
  if(!configuration)throw new DefinitionError('源配置缺失：'+configurationName,{path:template.source.path});
  if(template.representation?.mode==='deferred')return {solids:[],parameters:[],evaluated:{},parameterMs:0,featureCount:0,deferred:[{path:template.source.path,policyId:template.representation.policyId}]};
  if(ancestry.includes(template.source.path))throw new DefinitionError('装配引用形成循环');
  const chain=[...ancestry,template.source.path];
  const parameters=listParameters(template,configurationName);
  for(const [name,value] of Object.entries(overrides)){
    const parameter=parameters.find(item=>item.name===name);
    if(!parameter?.editable)throw new DefinitionError('尺寸没有可验证的离线驱动定义：'+name,{path:template.source.path});
    if(!Number.isFinite(value)||value<=0)throw new DefinitionError('长度必须是大于零的有限数：'+name);
  }
  let evaluated,equationReferences;
  try{const context=await equationInitialValues(template,configuration,loadTemplate);evaluated=solveEquations(configuration.equations,context.initialValues,overrides);equationReferences=context.referenceEvidence;}
  catch(error){error.source={...error.source,path:template.source.path,configuration:configurationName};throw error;}
  const activeGeometry=configuration.features.filter(feature=>!suppressedInTree(feature,configuration)&&!neutralType(feature.type)&&!consumedBend(feature,configuration));
  let drivenSketchDimensions=null;
  if(activeGeometry.length===1&&['Extrusion','Boss','SMBaseFlange'].includes(activeGeometry[0].type)){
    const drive=linearSketchDrive(configuration,activeGeometry[0]);
    if(drive){
      let solved;try{solved=drive.solve(overrides);}catch(error){error.source={path:template.source.path,configuration:configurationName,sketch:drive.profileName};throw error;}
      configuration={...configuration,features:configuration.features.map(feature=>feature.name===drive.profileName?{...feature,sketch:solved.sketch}:feature)};drivenSketchDimensions=solved.dimensionEdges;
      for(const parameter of parameters)if(Object.hasOwn(solved.values,parameter.name))parameter.value=solved.values[parameter.name];
    }
  }
  const parameterMs=performance.now()-start;
  if(configuration.components.length){
    if(Object.keys(overrides).length)throw new DefinitionError('装配配合参数求解尚未实现，禁止沿用旧安装矩阵');
    const solids=[],solidSources=[],deferred=[],schematic=[],curveQuality=[],componentMass=[],geometryTimings={cutFeatureMs:0,otherFeatureMs:0};let complexFeatureCount=0;
    try{for(const component of configuration.components){
      if(component.suppression===0)continue;
      const physicalScope=settings.componentScope==='all-physical',state=componentState(component);
      if(physicalScope&&!state.known)throw new DefinitionError('物理检查缺少原生组件状态：'+component.name);
      if(physicalScope&&state.envelope)continue;
      if(component.representation?.mode==='deferred'){deferred.push({path:component.path,component:component.name,policyId:component.representation.policyId});continue;}
      const childTemplate=component.isVirtual?{...component.embeddedDefinition,source:{...component.embeddedDefinition?.source,path:template.source.path+'#embedded/'+component.id}}:await loadTemplate(component.path);
      if(component.isVirtual&&!component.embeddedDefinition?.captured)throw new DefinitionError('内嵌组件原始定义缺失：'+component.name);
      const result=await evaluateTemplate(kernel,childTemplate,component.configuration,{},settings,loadTemplate,chain);
      componentMass.push({component,result:{massVolumes:result.massVolumes,massComponentScopeKnown:result.massComponentScopeKnown??false}});
      complexFeatureCount+=result.complexFeatureCount??0;
      curveQuality.push(...(result.curveQuality??[]));
      for(const key of Object.keys(geometryTimings))geometryTimings[key]+=result.geometryTimings?.[key]??0;
      deferred.push(...(result.deferred??[]));
      schematic.push(...(result.schematic??[]));
      try{if(physicalScope||state.visible)for(const [bodyIndex,solid]of result.solids.entries()){const placement=swMatrix(component.transform);solids.push(solid.transform(placement));const childSource=result.solidSources?.[bodyIndex]??{path:childTemplate.source.path,configuration:component.configuration,sha256:childTemplate.source.sha256,bodyIndex,componentChain:[]};solidSources.push({...childSource,appearanceRole:childSource.partName?childSource.appearanceRole:component.previewRole??childSource.appearanceRole,transform:multiplyMatrix(placement,childSource.transform??identityMatrix),componentChain:[component.name,...childSource.componentChain]});}}
      finally{result.solids.forEach(solid=>solid.delete());}
    }}catch(error){solids.forEach(solid=>solid.delete());throw error;}
    const mass=componentMassTotals(componentMass),nativeIncludeHidden=configuration.validation?.massPropertyEvidence?.nativeIncludeHidden;
    return {solids,solidSources,parameterMs,parameters,evaluated,equationReferences,featureCount:configuration.features.length,complexFeatureCount,curveQuality,deferred,schematic,geometryTimings,...mass,massVolumeMm3:mass.massComponentScopeKnown&&typeof nativeIncludeHidden==='boolean'?mass.massVolumes[nativeIncludeHidden?'includingHidden':'visible']:null};
  }
  const approvedLouver=feature=>feature.type==='FormToolInstance'&&feature.representation?.mode==='schematic'&&feature.schematicDefinition?.kind==='raised-louver';
  const unknownFeatures=configuration.features.filter(feature=>!suppressedInTree(feature,configuration)&&!neutralType(feature.type)&&!consumedBend(feature,configuration)&&!approvedLouver(feature)&&!['Extrusion','Boss','Cut','SMBaseFlange','EdgeFlange','CornerFeat','FlatPattern','WeldMemberFeat','MirrorPattern','MirrorSolid','LPattern','Chamfer','Revolution','RevCut'].includes(feature.type));
  if(unknownFeatures.length)throw new DefinitionError('尚未支持真实特征：'+unknownFeatures.map(feature=>feature.name+' ['+feature.type+']').join('；'),{path:template.source.path,configuration:configurationName,features:unknownFeatures.map(feature=>({name:feature.name,type:feature.type,id:feature.id}))});
  let solid=null;const featureTrace=[],featureHistory=new Map(),ownedHistory=[],schematic=(configuration.offlinePreviewApproximations??[]).map(row=>({path:template.source.path,...row})),curveQuality=[],geometryTimings={cutFeatureMs:0,otherFeatureMs:0};
  const bodyTracker=configuration.features.some(feature=>!suppressedInTree(feature,configuration)&&(feature.type==='MirrorSolid'||feature.definition?.properties.BodyPattern))?new NativeBodyTracker(configuration):null;
  function applyOperations(feature,operations){
    const retained=[],pending=new Set(operations.map(operation=>operation.solid));
    try{
      for(const operation of operations){const saved={kind:operation.kind,solid:operation.solid.transform(identityMatrix)};retained.push(saved);ownedHistory.push(saved);}
      featureHistory.set(feature.id,retained);featureHistory.set(feature.name,retained);
      if(bodyTracker){bodyTracker.apply(feature,operations);solid?.delete();solid=null;solid=bodyTracker.combined();return;}
      for(const operation of operations){
        if(!solid){if(operation.kind==='subtract')throw new DefinitionError('切除之前没有实体');solid=operation.solid;pending.delete(solid);}
        else{const next=operation.kind==='subtract'?solid.subtract(operation.solid):solid.add(operation.solid);solid.delete();solid=next;operation.solid.delete();pending.delete(operation.solid);}
      }
    }finally{for(const temporary of pending)temporary.delete();}
  }
  try{
    for(const feature of configuration.features){
      if(suppressedInTree(feature,configuration)||neutralType(feature.type)||consumedBend(feature,configuration))continue;
      const featureStarted=performance.now();try{
      const properties=feature.definition?.properties;
      const priorVolume=settings.traceFeatures&&solid?solid.volume():0;
      if(feature.type==='FlatPattern'){
        if(!solid)throw new DefinitionError('展平没有前置实体：'+feature.name);
        verifyUnbentFlatPattern(feature,configuration);
        if(settings.traceFeatures)featureTrace.push({name:feature.name,type:feature.type,operation:'identity-on-proven-unbent-sheet',priorVolume,volume:solid.volume(),bounds:solid.boundingBox()});
        continue;
      }
      if(feature.type==='EdgeFlange'){
        if(!solid)throw new DefinitionError('边线法兰没有前置实体：'+feature.name);
        const operations=edgeFlangeOperations(kernel,feature,configuration,{swMatrix,inverseRigidMatrix,sketchLoops},settings.arcSegments??24);
        if(operations.previewApproximation)schematic.push({path:template.source.path,feature:feature.name,policyId:'user-20261103-schematic-sheet-metal-corners',...operations.previewApproximation});
        if(operations.stitchedSlits?.length)schematic.push({path:template.source.path,feature:feature.name,policyId:'user-20261103-stitch-slits-below-0.1mm',kind:'stitched-flange-ends',profiles:operations.stitchedSlits,sourceParametersPreserved:true});
        applyOperations(feature,operations);
        if(settings.traceFeatures)featureTrace.push({name:feature.name,type:feature.type,priorVolume,volume:solid.volume(),bounds:solid.boundingBox()});
        continue;
      }
      if(feature.type==='CornerFeat'){
        if(!solid||!properties||!Number.isFinite(properties.GapDistance)||properties.GapDistance<0)throw new DefinitionError('闭合角缺少前置折边或原间隙：'+feature.name);
        schematic.push({path:template.source.path,feature:feature.name,policyId:'user-20261103-schematic-sheet-metal-corners',kind:'closed-corner',gapMm:properties.GapDistance*1000,cornerType:properties.CornerType,method:'retain-flange-intersection-as-corner-illustration'});
        if(settings.traceFeatures)featureTrace.push({name:feature.name,type:feature.type,operation:'schematic-corner',priorVolume,volume:solid.volume(),bounds:solid.boundingBox()});
        continue;
      }
      if(approvedLouver(feature)){
        const footprint=configuration.features.find(candidate=>candidate.id===feature.schematicDefinition.footprintSketchId);
        if(!footprint?.sketch)throw new DefinitionError('百叶窗示意缺少原始放置轮廓：'+feature.name);
        const sourceSheets=configuration.features.filter(candidate=>!candidate.suppressed&&candidate.type==='SheetMetal'&&feature.parents.includes(candidate.name)&&candidate.definition?.properties.Thickness>0);
        if(sourceSheets.length!==1)throw new DefinitionError('百叶窗示意未能唯一对应原始钣金特征：'+feature.name);
        const sheet=sourceSheets[0];
        applyOperations(feature,louverOperations(kernel,feature,footprint.sketch,sheet.definition.properties.Thickness*1000,sketchLoops(footprint.sketch,settings.arcSegments??24),inverseRigidMatrix(swMatrix(footprint.sketch.modelToSketch))));
        schematic.push({path:template.source.path,feature:feature.name,policyId:feature.representation.policyId,mode:'raised-louver',construction:'rectangular-block'});
        if(settings.traceFeatures)featureTrace.push({name:feature.name,type:feature.type,representation:'schematic',priorVolume,volume:solid.volume(),bounds:solid.boundingBox()});
        continue;
      }
      if(feature.type==='Chamfer'){
        applyOperations(feature,chamferOperations(kernel,feature));
        if(settings.traceFeatures)featureTrace.push({name:feature.name,type:feature.type,priorVolume,volume:solid.volume(),bounds:solid.boundingBox()});
        continue;
      }
      if(!properties)throw new DefinitionError('真实特征参数缺失：'+feature.name,{path:template.source.path});
      if(['MirrorPattern','MirrorSolid','LPattern'].includes(feature.type)){
        if(feature.type==='MirrorPattern'&&feature.parents.filter(parentName=>configuration.features.some(parent=>parent.name===parentName&&parent.type==='RefPlane')).length>1&&!properties.secondaryMirrorDefinition)throw new DefinitionError('双平面镜像的第二平面及种子模式尚未完整采集：'+feature.name);
        applyOperations(feature,bodyTracker?bodyTracker.patternOperations(feature,swMatrix):patternOperations(feature,featureHistory,swMatrix));
        if(settings.traceFeatures)featureTrace.push({name:feature.name,type:feature.type,priorVolume,volume:solid.volume(),bounds:solid.boundingBox()});
        continue;
      }
      const parentSketches=configuration.features.filter(candidate=>candidate.sketch&&feature.parents.includes(candidate.name));
      let sketchFeature=parentSketches[0];
      if(parentSketches.length>1&&feature.type==='Cut'&&!properties.isThin&&!properties.Contours?.length){
        // Parent dependencies can include open reference sketches. A solid cut
        // consumes the uniquely complete native closed profile, not the first
        // sketch encountered in the feature list.
        const closed=parentSketches.filter(candidate=>{const sketch=candidate.sketch,physical=sketch.segments.filter(segment=>!segment.construction),contours=sketch.contours??[];return physical.length&&contours.length&&contours.every(contour=>contour.closed)&&physical.every(segment=>contours.some(contour=>contour.segments.includes(segment.id)));});
        // SW records transitive dependencies too: the cut profile can refer to
        // the closed base sketch. Exclude those ancestors using the captured
        // dependency graph, without relying on feature or parent array order.
        const dependsOn=(candidate,ancestorName,visited=new Set())=>{
          if(visited.has(candidate.name))return false;
          visited.add(candidate.name);
          return (candidate.parents??[]).some(parentName=>parentName===ancestorName||configuration.features.some(parent=>parent.name===parentName&&dependsOn(parent,ancestorName,visited)));
        };
        const profiles=closed.filter(candidate=>!closed.some(other=>other!==candidate&&dependsOn(other,candidate.name)));
        if(profiles.length!==1)throw new DefinitionError('实体切除未对应唯一完整原轮廓：'+feature.name);
        sketchFeature=profiles[0];
      }
      if(!sketchFeature)throw new DefinitionError('特征没有可追溯的源草图：'+feature.name);
      if(['Revolution','RevCut'].includes(feature.type)){
        if(properties.FeatureScopeBodies?.length){
          if(!solid)throw new DefinitionError('旋转指定作用实体尚未生成：'+feature.name);
          const bodies=solid.decompose();
          try{if(bodies.length!==1||properties.FeatureScopeBodies.length!==1)throw new DefinitionError('旋转切除的多实体作用范围尚未支持：'+feature.name);}
          finally{bodies.forEach(body=>body.delete());}
        }
        const profileQuality={segments:settings.complexArcSegments??settings.arcSegments??24,chordToleranceMm:(settings.chordToleranceMm??.3)/2};
        const groups=selectedProfileGroups(sketchFeature.sketch,properties,profileQuality,feature.name);
        const revolved=revolveFeature(kernel,feature,sketchFeature.sketch,groups,inverseRigidMatrix(swMatrix(sketchFeature.sketch.modelToSketch)),profileQuality.segments,settings.chordToleranceMm??.3,curveQuality);
        applyOperations(feature,[{kind:feature.type==='RevCut'?'subtract':'add',solid:revolved}]);
        if(settings.traceFeatures)featureTrace.push({name:feature.name,type:feature.type,priorVolume,volume:solid.volume(),bounds:solid.boundingBox()});
        continue;
      }
      let tool;
      if(feature.type==='SMBaseFlange'&&sketchFeature.sketch.contours?.some(contour=>!contour.closed)){
        const orientationWitnesses=[];
        for(const cut of configuration.features.filter(candidate=>(candidate.previewOmittedHole||!suppressedInTree(candidate,configuration))&&candidate.type==='Cut'&&candidate.parents.includes(feature.name)&&candidate.definition?.properties.NormalCut)){
          const attachedSketches=configuration.features.filter(candidate=>candidate.sketch&&cut.parents.includes(candidate.name)&&candidate.parents.length===1&&candidate.parents[0]===feature.name);
          for(const attached of attachedSketches){
            const attachedToBase=multiplyMatrix(swMatrix(sketchFeature.sketch.modelToSketch),inverseRigidMatrix(swMatrix(attached.sketch.modelToSketch)));
            if(Math.abs(attachedToBase[10])>1e-8)continue;
            for(const segment of attached.sketch.segments.filter(candidate=>!candidate.construction))for(const point of [segment.geometry.start,segment.geometry.end])orientationWitnesses.push(transformPoint(point.map(value=>value*1000),attachedToBase).slice(0,2));
          }
        }
        const localFlange=openBaseFlange(kernel,sketchFeature.sketch,properties,settings.arcSegments??24,orientationWitnesses);
        try{tool=localFlange.transform(inverseRigidMatrix(swMatrix(sketchFeature.sketch.modelToSketch)));}finally{localFlange.delete();}
      }else if(feature.type==='WeldMemberFeat'){
        const profile=configuration.features.find(candidate=>candidate.parent===feature.name&&candidate.sketch);
        if(!profile)throw new DefinitionError('焊件型材草图未导出：'+feature.name);
        const pathSegments=sketchFeature.sketch.segments.filter(segment=>!segment.construction);
        if(pathSegments.length!==1||pathSegments[0].type!==0)throw new DefinitionError('多段焊件修剪尚未支持：'+feature.name);
        const startPoint=transformPoint(pathSegments[0].geometry.start.map(value=>value*1000),inverseRigidMatrix(swMatrix(sketchFeature.sketch.modelToSketch)));
        const endPoint=transformPoint(pathSegments[0].geometry.end.map(value=>value*1000),inverseRigidMatrix(swMatrix(sketchFeature.sketch.modelToSketch)));
        const profileToModel=inverseRigidMatrix(swMatrix(profile.sketch.modelToSketch));
        const zAxis=[profileToModel[8],profileToModel[9],profileToModel[10]],pathDirection=difference3(endPoint,startPoint);
        const depth=length3(pathDirection);if(Math.abs(Math.abs(zAxis.reduce((sum,value,index)=>sum+value*pathDirection[index]/depth,0))-1)>1e-5)throw new DefinitionError('型材轴与路径不垂直：'+feature.name);
        const pathInProfile=transformPoint(startPoint,swMatrix(profile.sketch.modelToSketch));
        const endInProfile=transformPoint(endPoint,swMatrix(profile.sketch.modelToSketch));
        const section=new kernel.CrossSection(sketchLoops(profile.sketch,settings.arcSegments??24),'EvenOdd');
        try{
          const extruded=section.extrude(depth);
          try{const translated=extruded.translate([0,0,Math.min(pathInProfile[2],endInProfile[2])]);try{tool=translated.transform(profileToModel);}finally{translated.delete();}}finally{extruded.delete();}
        }finally{section.delete();}
      }else{
        let groups=selectedProfileGroups(sketchFeature.sketch,properties,settings.arcSegments??24,feature.name);
        if(feature.type==='Cut'){
          const stitching=stitchPreviewSlitGroups(groups);groups=stitching.groups;
          if(stitching.stitched.length)schematic.push({path:template.source.path,feature:feature.name,policyId:'user-20261103-stitch-slits-below-0.1mm',kind:'stitched-straight-slits',slits:stitching.stitched,sourceParametersPreserved:true});
          if(!groups.length){if(settings.traceFeatures)featureTrace.push({name:feature.name,type:feature.type,operation:'schematic-slit-stitch',priorVolume,volume:solid.volume(),bounds:solid.boundingBox()});continue;}
        }
        const loops=groups.flat();
        let depth,offset=0;
        if(feature.type==='SMBaseFlange'){
          if(!properties.Thickness)throw new DefinitionError('钣金厚度缺失：'+feature.name);
          depth=properties.Thickness*1000;offset=properties.ReverseThickness?0:-depth;
          if(properties.SymmetricThickness)offset=-depth/2;
        }else{
          const condition=properties.forwardEndCondition;
          const depthDimension=parameters.find(parameter=>parameter.name.endsWith('@'+feature.name)&&parameter.editable);
          depth=(depthDimension&&Object.hasOwn(overrides,depthDimension.name)?overrides[depthDimension.name]:properties.forwardDepth*1000);
          if(properties.isThin)throw new DefinitionError('薄壁拉伸尚未支持：'+feature.name);
          if(properties.FlipSideToCut)throw new DefinitionError('反侧切除尚未支持：'+feature.name);
          if(properties.forwardDraftEnabled||properties.reverseDraftEnabled)throw new DefinitionError('拔模拉伸尚未支持：'+feature.name);
          if(properties.directionReferences?.some(reference=>reference.reference))throw new DefinitionError('指定参考方向拉伸尚未支持：'+feature.name);
          if(properties.FromType!==0)throw new DefinitionError('非草图起始拉伸尚未支持：'+feature.name);
          if(condition===2){
            if(!solid||ownedHistory.filter(operation=>operation.kind==='add').length!==1)throw new DefinitionError('成形后多实体的贯穿下一面尚未支持：'+feature.name);
            // In a single convex body each ray has one material interval, so the first exit is the last exit.
            const hull=solid.hull();try{const missing=hull.subtract(solid);try{if(!missing.isEmpty())throw new DefinitionError('非凸实体的贯穿下一面尚未支持：'+feature.name);}finally{missing.delete();}}finally{hull.delete();}
          }
          if(condition===1||condition===2){
            if(!solid)throw new DefinitionError('贯穿切除没有前置实体');
            const bounds=solid.boundingBox(),sketchToModel=inverseRigidMatrix(swMatrix(sketchFeature.sketch.modelToSketch));
            const reverse=feature.type==='Cut'?!properties.ReverseDirection:properties.ReverseDirection;
            const direction=sketchToModel.slice(8,11).map(value=>reverse?-value:value),origin=sketchToModel.slice(12,15);
            const corners=Array.from({length:8},(_,mask)=>[0,1,2].map(axis=>(mask&(1<<axis))?bounds.max[axis]:bounds.min[axis]));
            // Through-all is bounded by the prior solid, never a substituted part shape.
            depth=Math.max(...corners.map(corner=>direction.reduce((sum,value,index)=>sum+value*(corner[index]-origin[index]),0)))+1e-4;offset=reverse?-depth:0;
          }else if(condition===4){
            const target=referencePlane(properties.forwardEndReference),sketchToModel=inverseRigidMatrix(swMatrix(sketchFeature.sketch.modelToSketch));
            const sign=(feature.type==='Cut'?!properties.ReverseDirection:properties.ReverseDirection)?-1:1;
            const direction=sketchToModel.slice(8,11).map(value=>value*sign);
            const projection=target.normal.reduce((sum,value,index)=>sum+value*direction[index],0);
            if(Math.abs(projection)<1e-8)throw new DefinitionError('终止平面与拉伸方向平行：'+feature.name);
            const distances=loops.flat().map(point=>{
              const origin=transformPoint([...point,0],sketchToModel);
              return target.normal.reduce((sum,value,index)=>sum+value*(target.origin[index]-origin[index]),0)/projection;
            });
            if(Math.min(...distances)<=1e-7)throw new DefinitionError('终止平面穿越草图或位于拉伸反向：'+feature.name);
            depth=Math.max(...distances)+1;offset=sign<0?-depth:0;
          }else if(condition===6)offset=-depth/2;
          else if(condition!==0)throw new DefinitionError('拉伸终止条件尚未支持：'+condition+' / '+feature.name);
          if(properties.BothDirections&&condition!==6)throw new DefinitionError('双向非对称拉伸尚未支持：'+feature.name);
          if((feature.type==='Cut'?!properties.ReverseDirection:properties.ReverseDirection)&&condition!==1&&condition!==2&&condition!==6)offset=-depth;
        }
        if(!(depth>0))throw new DefinitionError('特征深度无效：'+feature.name);
        if(activeGeometry.length===1){
          const sketchToModel=inverseRigidMatrix(swMatrix(sketchFeature.sketch.modelToSketch));
          for(const parameter of parameters.filter(parameter=>parameter.editable)){
            if(drivenSketchDimensions?.[parameter.name]){
              parameter.annotationEdges=[offset,offset+depth].map(height=>drivenSketchDimensions[parameter.name].map(point=>transformPoint([point[0],point[1],height],sketchToModel)));
              parameter.annotationSource='native-distance-relation-points';
            }else if(parameter.name.endsWith('@'+feature.name)){
              parameter.annotationEdges=sketchFeature.sketch.segments.filter(segment=>segment.type===0&&!segment.construction).flatMap(segment=>[segment.geometry.start,segment.geometry.end]).map(point=>[offset,offset+depth].map(height=>transformPoint([point[0]*1000,point[1]*1000,height],sketchToModel)));
              parameter.annotationSource='source-profile-extrusion-edges';
            }
          }
        }
        const section=unionProfileSections(kernel,groups);
        try{
          const extrusion=section.extrude(depth),positioned=extrusion.translate([0,0,offset]);extrusion.delete();
          try{tool=positioned.transform(inverseRigidMatrix(swMatrix(sketchFeature.sketch.modelToSketch)));}finally{positioned.delete();}
          if(properties.forwardEndCondition===4){
            const target=referencePlane(properties.forwardEndReference),sketchToModel=inverseRigidMatrix(swMatrix(sketchFeature.sketch.modelToSketch));
            const origin=transformPoint([0,0,0],sketchToModel),side=target.normal.reduce((sum,value,index)=>sum+value*(origin[index]-target.origin[index]),0)>0?1:-1;
            const normal=target.normal.map(value=>value*side),planeOffset=normal.reduce((sum,value,index)=>sum+value*target.origin[index],0);
            const clipped=tool.trimByPlane(normal,planeOffset);tool.delete();tool=clipped;
          }
        }finally{section.delete();}
      }
      applyOperations(feature,[{kind:feature.type==='Cut'?'subtract':'add',solid:tool}]);
      if(settings.traceFeatures)featureTrace.push({name:feature.name,type:feature.type,priorVolume,volume:solid.volume(),bounds:solid.boundingBox()});
      }finally{geometryTimings[['Cut','RevCut'].includes(feature.type)?'cutFeatureMs':'otherFeatureMs']+=performance.now()-featureStarted;}
    }
    if(!solid||solid.isEmpty())throw new DefinitionError('模板未产生有效实体');
    if(solid.status()!=='NoError')throw new DefinitionError('模板实体拓扑无效：'+solid.status());
    const outputSolids=bodyTracker?bodyTracker.exportSolids():[solid],volume=outputSolids.reduce((sum,body)=>sum+body.volume(),0);
    if(bodyTracker){solid.delete();solid=null;}
    return {solids:outputSolids,parameterMs,parameters,evaluated,equationReferences,featureCount:configuration.features.length,complexFeatureCount:configuration.features.filter(feature=>!suppressedInTree(feature,configuration)&&['Revolution','RevCut'].includes(feature.type)).length,curveQuality,featureTrace,geometryTimings,bodySelections:bodyTracker?.selections,deferred:[],schematic,massVolumes:{visible:volume,includingHidden:volume},massComponentScopeKnown:true};
  }catch(error){solid?.delete();if(!error.source?.path)error.source={...error.source,path:template.source.path,configuration:configurationName};throw error;}
  finally{for(const operation of ownedHistory)operation.solid.delete();bodyTracker?.dispose();}
}
export function solidToMesh(solid){
  const sourceMesh=solid.getMesh();const positions=new Float64Array(sourceMesh.numVert*3);
  for(let index=0;index<sourceMesh.numVert;index++)for(let axis=0;axis<3;axis++)positions[index*3+axis]=sourceMesh.vertProperties[index*sourceMesh.numProp+axis];
  const patches=planarMixedMesh(positions,sourceMesh.triVerts,meshFaceLabels(sourceMesh));
  return {positions:patches.positions,indices:patches.renderIndices,outlinePositions:Float32Array.from(positions),outlineTriangles:Uint32Array.from(sourceMesh.triVerts),patches:{quads:patches.quadIndices,triangles:patches.triangleIndices},quadCount:patches.quadCount,trianglePatchCount:patches.trianglePatchCount,equivalentTriangleCount:patches.equivalentTriangleCount,planarPartitions:patches.planarPartitions,volume:solid.volume(),triangleCount:patches.renderIndices.length/3};
}
