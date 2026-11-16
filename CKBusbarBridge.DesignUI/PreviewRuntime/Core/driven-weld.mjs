import {DefinitionError,solveEquations} from './expressions.mjs';
import {linearSketchDrive,solveLinearSystem} from './linear-sketch-drive.mjs';
import {activeTemplateGeometry,swMatrix,inverseRigidMatrix,transformPoint} from './geometry.mjs';
import {transformDirection} from './template-planes.mjs';
import {reflectionMatrix} from './feature-patterns.mjs';
import {axisSketchDrive} from './axis-sketch-drive.mjs';
import {capturedDimensionValues} from './equation-context.mjs';
import {driveLinearPattern} from './driven-patterns.mjs';
import {driveRigidSlotSketch,redundantUnresolvedSlotFace} from './rigid-slot-drive.mjs';
import {driveSchematicBaseCut} from './schematic-base-cut.mjs';
import {driveSmallOffsetAngleProfile,resolveSmallOffsetAngleDependency} from './driven-angle-profile.mjs';

const dot=(left,right)=>left.reduce((sum,value,index)=>sum+value*right[index],0),subtract=(left,right)=>left.map((value,index)=>value-right[index]),norm=vector=>Math.hypot(...vector),mm=point=>point.map(value=>value*1000),meters=point=>point.map(value=>value/1000);
const canonical=name=>name.split('@').slice(0,-1).join('@');
const near=(left,right,tolerance=1e-6)=>norm(subtract(left,right))<tolerance;

function circularSketch(sketch,modelLine,allowOrigin=false){
  if(sketch.segments.some(segment=>segment.construction||segment.type!==1||!near(segment.geometry.start,segment.geometry.end,1e-9)))throw new DefinitionError('焊件后续草图尚不支持非整圆轮廓');
  const points=sketch.points,variables=points.length*2,rows=[],indices=new Map(points.map((point,index)=>[point.id,index])),modelToSketch=swMatrix(sketch.modelToSketch);
  const add=(terms,value)=>{const row=Array(variables+1).fill(0);for(const [identity,axis,coefficient]of terms){if(!indices.has(identity))throw new DefinitionError('圆孔关系缺少原始圆心');row[indices.get(identity)*2+axis]+=coefficient;}row[variables]=value;rows.push(row);};
  for(const relation of sketch.relations.filter(row=>!row.suppressed)){
    const dimension=relation.dimension;
    if([25,26].includes(relation.type)){
      if(relation.entities.length!==2)throw new DefinitionError('圆心水平或竖直关系缺少两点');
      const axis=relation.type===25?1:0,local=relation.entities.filter(entity=>indices.has(entity.id)),external=relation.entities.filter(entity=>!indices.has(entity.id));
      if(local.length===2)add([[local[0].id,axis,1],[local[1].id,axis,-1]],0);
      else if(local.length===1&&external.length===1&&allowOrigin&&external[0].point&&near(external[0].point,[0,0,0],1e-10))add([[local[0].id,axis,1]],0);
      else throw new DefinitionError('圆心水平或竖直关系未引用原始圆心与固定原点');
      continue;
    }
    if(relation.type===15){const segment=sketch.segments.find(row=>row.id===relation.entities[0]?.id);if(!segment||relation.entities.length!==1||!dimension||Math.abs(dimension.value-2*segment.geometry.radius)>1e-8)throw new DefinitionError('圆孔直径与原始整圆不符');continue;}
    if(relation.type!==1||!dimension||relation.entities.length!==2||![1,2].includes(dimension.drivenState))throw new DefinitionError('焊件圆孔含尚未支持的定位关系');
    const localPoints=relation.entities.filter(entity=>indices.has(entity.id));
    if(localPoints.length===2){
      const [first,second]=localPoints,delta=subtract(mm(second.point),mm(first.point)),axis=Math.abs(delta[1])<1e-6?0:Math.abs(delta[0])<1e-6?1:-1;
      if(axis<0||Math.abs(Math.abs(delta[axis])-dimension.value*1000)>1e-5)throw new DefinitionError('圆心距离不是可验证的轴向长度');
      const sign=Math.sign(delta[axis]);add([[second.id,axis,sign],[first.id,axis,-sign]],dimension.value*1000);
    }else if(localPoints.length===1){
      const point=localPoints[0],reference=relation.entities.find(entity=>!indices.has(entity.id));
      if(!reference.lineParameters)throw new DefinitionError('圆孔定位缺少原始直线参考');
      const source=reference.lineParameters,origin=transformPoint(mm(source.slice(0,3)),modelToSketch),direction=transformDirection(source.slice(3,6),modelToSketch),axis=Math.abs(direction[0])<1e-8&&Math.abs(direction[1])>1-1e-8?0:Math.abs(direction[1])<1e-8&&Math.abs(direction[0])>1-1e-8?1:-1;
      if(axis<0)throw new DefinitionError('圆孔参考线在源草图内不是唯一轴向直线');
      const distance=point.point[axis]*1000-origin[axis],sign=Math.sign(distance);
      if(!sign||Math.abs(Math.abs(distance)-dimension.value*1000)>1e-5)throw new DefinitionError('圆孔原始距边关系无法核验');
      const moved=transformPoint(modelLine(reference).origin,modelToSketch);add([[point.id,axis,1]],moved[axis]+sign*dimension.value*1000);
    }else throw new DefinitionError('圆孔关系未对应当前草图圆心');
  }
  const solved=solveLinearSystem(rows,variables),updated=structuredClone(sketch),changed=new Map();
  for(const [index,point]of updated.points.entries()){const coordinates=[solved[index*2]/1000,solved[index*2+1]/1000,0];changed.set(point.id,coordinates);point.coordinates=coordinates;}
  for(const segment of updated.segments){const original=sketch.segments.find(row=>row.id===segment.id),centers=points.filter(point=>near(point.coordinates,original.geometry.center,1e-9));if(centers.length!==1)throw new DefinitionError('整圆没有唯一原始圆心');const center=changed.get(centers[0].id),delta=subtract(center,segment.geometry.center);for(const key of ['center','start','end'])segment.geometry[key]=segment.geometry[key].map((value,index)=>value+delta[index]);}
  return updated;
}

// Re-solve the captured single straight member and its end-based cut constraints.
// Unsupported references fail before the derived definition can be evaluated.
export function driveStraightWeldDefinition(template,configurationName,drive,{documentModel=null}={}){
  if(drive?.source!=='captured-governing-assembly-equation'||drive.templatePath!==template.source.path||drive.configuration!==configurationName||!Number.isFinite(drive.value)||drive.value<=0)throw new DefinitionError('焊件长度必须来自准确绑定的原始装配方程');
  const sourceConfiguration=template.configurations.find(row=>row.name===configurationName),configuration=documentModel?structuredClone(sourceConfiguration):sourceConfiguration;
  for(const state of documentModel?.featureStates??[]){const feature=configuration.features.find(row=>row.name===state.feature);if(!feature)throw new DefinitionError('焊件原压缩目标缺失');feature.suppressed=state.suppressed;}
  const geometry=activeTemplateGeometry(configuration);
  if(configuration.components.length||geometry[0]?.type!=='WeldMemberFeat'||geometry.slice(1).some(row=>!['Cut','MirrorPattern',...(documentModel?['LPattern','Chamfer','Boss']:[])].includes(row.type)))throw new DefinitionError('焊件受驱特征链尚未完整支持');
  const captured=capturedDimensionValues(configuration),activeEquations=configuration.equations.filter(row=>!row.disabled&&row.userDisposition?.mode!=='void-by-user'),equationTargets=activeEquations.map(row=>/^\s*"([^"]+)"\s*=/.exec(row.expression)?.[1]);
  if(!documentModel&&equationTargets.some(name=>!name||name===drive.dimension||name.split('@').length!==2||!Object.hasOwn(captured,name)||configuration.features.flatMap(row=>row.dimensions).filter(row=>canonical(row.name)===name).some(row=>row.dimensionType!==0)))throw new DefinitionError('焊件内部方程未对应原始局部长度尺寸');
  if(documentModel&&(documentModel.templatePath!==template.source.path||documentModel.configuration!==configurationName||documentModel.sourceSha256!==template.source.sha256))throw new DefinitionError('焊件完整文档驱动来源不一致');
  const baselineEquations=documentModel?null:solveEquations(activeEquations,captured),solvedEquations=documentModel?Object.fromEntries(documentModel.dimensions.map(row=>[row.dimension,row.valueMm??row.value])):solveEquations(activeEquations,captured,{[drive.dimension]:drive.value}),dimensionOverrides=documentModel?{...solvedEquations}:{[drive.dimension]:drive.value};
  if(documentModel){if(Math.abs(dimensionOverrides[drive.dimension]-drive.value)>1e-7||Object.entries(dimensionOverrides).some(([name,value])=>!Object.hasOwn(captured,name)||!Number.isFinite(value)))throw new DefinitionError('焊件完整驱动未对应原始尺寸');}
  else for(const name of equationTargets){if(Math.abs(baselineEquations[name]-captured[name])>1e-5||!Number.isFinite(solvedEquations[name])||solvedEquations[name]<=0)throw new DefinitionError('焊件内部方程不能复现原始尺寸或生成有效长度');dimensionOverrides[name]=solvedEquations[name];}
  // The native local equations have now been solved explicitly. The sketch
  // solver receives their governing values rather than silently freezing them.
  const constraintConfiguration={...configuration,equations:[]},consumedDimensions=new Set([drive.dimension]);
  const member=geometry[0],paths=configuration.features.filter(row=>member.parents.includes(row.name)&&row.sketch&&row.sketch.segments.length===1&&row.sketch.segments[0].type===0&&row.sketch.contours?.length===1&&!row.sketch.contours[0].closed);
  if(paths.length!==1)throw new DefinitionError('焊件没有唯一的原始直线路径');
  const path=paths[0],pathDrive=linearSketchDrive(constraintConfiguration,member,{profileName:path.name,governingDimensions:[drive.dimension]});
  if(!pathDrive?.dimensions.some(row=>row.name===drive.dimension&&row.governed)||Math.abs(pathDrive.dimensions.find(row=>row.name===drive.dimension).value-drive.sourceValue)>1e-6)throw new DefinitionError('装配方程目标未对应焊件的原始线性路径尺寸');
  const solved=pathDrive.solve({[drive.dimension]:drive.value}),pathToModel=inverseRigidMatrix(swMatrix(path.sketch.modelToSketch)),oldSegment=path.sketch.segments[0],newSegment=solved.sketch.segments[0],oldEnds=[oldSegment.geometry.start,oldSegment.geometry.end].map(point=>transformPoint(mm(point),pathToModel)),newEnds=[newSegment.geometry.start,newSegment.geometry.end].map(point=>transformPoint(mm(point),pathToModel)),length=norm(subtract(oldEnds[1],oldEnds[0])),axis=subtract(oldEnds[1],oldEnds[0]).map(value=>value/length),deltas=newEnds.map((point,index)=>subtract(point,oldEnds[index]));
  if(!near(oldEnds[0].map((value,index)=>(value+oldEnds[1][index])/2),newEnds[0].map((value,index)=>(value+newEnds[1][index])/2))||deltas.some(delta=>norm(subtract(delta,axis.map(value=>value*dot(delta,axis))))>1e-6))throw new DefinitionError('焊件路径修改不是原约束确定的居中轴向长度变化');
  const endIndex=point=>{const station=dot(subtract(point,oldEnds[0]),axis);return Math.abs(station)<1e-5?0:Math.abs(station-length)<1e-5?1:-1;};
  const lineMotions=[];
  const moveLine=reference=>{
    const parameters=reference.lineParameters,origin=mm(parameters.slice(0,3)),direction=parameters.slice(3,6),parallel=Math.abs(dot(direction,axis));
    const candidates=lineMotions.filter(row=>reference.start&&reference.end&&((near(mm(reference.start),row.start)&&near(mm(reference.end),row.end))||(near(mm(reference.end),row.start)&&near(mm(reference.start),row.end))));
    if(candidates.length){const deltas=candidates.map(row=>subtract(row.newStart,row.start));if(deltas.some(delta=>!near(delta,deltas[0]))||candidates.some(row=>!near(subtract(row.newEnd,row.end),deltas[0])))throw new DefinitionError('焊件后续直边对应多个不同位移或改变了形状');return {origin:origin.map((value,index)=>value+deltas[0][index]),direction,start:mm(reference.start).map((value,index)=>value+deltas[0][index]),end:mm(reference.end).map((value,index)=>value+deltas[0][index])};}
    if(Math.abs(parallel-1)<1e-8)return {origin:origin.map((value,index)=>value+(angleProfile?.pointDisplacement(origin)[index]??0)),direction};
    const first=reference.start&&endIndex(mm(reference.start)),last=reference.end&&endIndex(mm(reference.end));
    if(parallel>1e-8||!Number.isInteger(first)||first<0||first!==last)throw new DefinitionError('焊件后续参考边未唯一落在原始端平面',{path:template.source.path,start:reference.start,end:reference.end,axis,oldEnds});
    return {origin:origin.map((value,index)=>value+deltas[first][index]+(angleProfile?.pointDisplacement(origin)[index]??0)),direction};
  };
  const derived=structuredClone(template),targetConfiguration=derived.configurations.find(row=>row.name===configurationName),evidence=[],planeMotions=oldEnds.map((point,index)=>({point,normal:axis,newPoint:newEnds[index]})),cylinderMotions=[],featureReferenceMotions=new Map();delete targetConfiguration.validation;
  const angleProfile=documentModel?driveSmallOffsetAngleProfile(template,configuration,documentModel,targetConfiguration):null;
  if(angleProfile){consumedDimensions.add(angleProfile.dimension);evidence.push(angleProfile.evidence);}
  const registerFeatureMotions=(feature,motions)=>{featureReferenceMotions.set(feature.id,motions);featureReferenceMotions.set(feature.name,motions);};
  const transformedMotions=(motions,originalMatrix,updatedMatrix)=>({planes:motions.planes.map(row=>({...row,point:transformPoint(row.point,originalMatrix),newPoint:transformPoint(row.newPoint,updatedMatrix),normal:transformDirection(row.normal,originalMatrix)})),lines:motions.lines.map(row=>({...row,start:transformPoint(row.start,originalMatrix),end:transformPoint(row.end,originalMatrix),newStart:transformPoint(row.newStart,updatedMatrix),newEnd:transformPoint(row.newEnd,updatedMatrix)})),cylinders:motions.cylinders.map(row=>({...row,point:transformPoint(row.point,originalMatrix),newPoint:transformPoint(row.newPoint,updatedMatrix),axis:transformDirection(row.axis,originalMatrix)}))});
  const addMotions=motions=>{planeMotions.push(...motions.planes);lineMotions.push(...motions.lines);cylinderMotions.push(...motions.cylinders);};
  for(const state of documentModel?.featureStates??[])targetConfiguration.features.find(row=>row.name===state.feature).suppressed=state.suppressed;
  targetConfiguration.features.find(row=>row.id===path.id).sketch=solved.sketch;
  for(const feature of geometry.slice(1)){
    if(feature.type==='Chamfer'){
      const properties=feature.definition.properties,updated=targetConfiguration.features.find(row=>row.id===feature.id).definition.properties;
      const pointDelta=point=>{const matches=planeMotions.filter(row=>Math.abs(Math.abs(dot(row.normal,axis))-1)<1e-7&&Math.abs(dot(row.normal,subtract(point,row.point)))<1e-5).map(row=>subtract(row.newPoint,row.point));if(!matches.length||matches.some(delta=>!near(delta,matches[0])))throw new DefinitionError('焊件倒角端点没有唯一原端面或切口平面');return matches[0];};
      updated.Edges=properties.Edges.map(edge=>{
        if(!edge.start||!edge.end||!edge.lineParameters||edge.adjacentFaces?.some(face=>!face.planeParameters))throw new DefinitionError('焊件倒角没有原始直边及平面');const result=structuredClone(edge),start=mm(edge.start),end=mm(edge.end),newStart=start.map((value,index)=>value+pointDelta(start)[index]),newEnd=end.map((value,index)=>value+pointDelta(end)[index]);
        result.start=meters(newStart);result.end=meters(newEnd);const length=norm(subtract(newEnd,newStart));result.lineParameters=[...result.start,...subtract(newEnd,newStart).map(value=>value/length)];
        for(const face of result.adjacentFaces){const normal=face.planeParameters.slice(0,3),point=mm(face.planeParameters.slice(3,6)),delta=Math.abs(dot(normal,axis))<1e-7?[0,0,0]:pointDelta(point);face.planeParameters=[...normal,...meters(point.map((value,index)=>value+delta[index]))];}
        return result;
      });evidence.push({feature:feature.name,source:'native-chamfer-edges-and-driven-end-planes'});continue;
    }
    if(feature.type==='LPattern'){
      const result=driveLinearPattern(feature,dimensionOverrides),index=targetConfiguration.features.findIndex(row=>row.id===feature.id);targetConfiguration.features[index]=result.feature;
      const seedMotions=(feature.definition.properties.PatternFeatureArray??[]).map(seed=>featureReferenceMotions.get(seed.id)??featureReferenceMotions.get(seed.feature));
      if(seedMotions.some(motions=>!motions))throw new DefinitionError('焊件阵列原种子缺少受驱引用几何');
      const copies={planes:[],lines:[],cylinders:[]},inner=result.evidence.nativeInnerAxis-1,oldCounts=result.evidence.sourceCounts,newCounts=result.evidence.counts;
      for(const [instanceIndex,transform]of feature.definition.properties.instanceTransforms.entries()){
        if(instanceIndex===0)continue;const stations=[];stations[inner]=instanceIndex%oldCounts[inner];stations[1-inner]=Math.floor(instanceIndex/oldCounts[inner]);if(stations.some((station,axis)=>station>=newCounts[axis]))continue;
        const updatedIndex=stations[1-inner]*newCounts[inner]+stations[inner],originalMatrix=swMatrix(transform),updatedMatrix=swMatrix(result.feature.definition.properties.instanceTransforms[updatedIndex]);
        for(const motions of seedMotions){const transformed=transformedMotions(motions,originalMatrix,updatedMatrix);for(const kind of Object.keys(copies))copies[kind].push(...transformed[kind]);}
      }
      addMotions(copies);registerFeatureMotions(feature,copies);
      for(const dimension of feature.dimensions)if(Object.hasOwn(dimensionOverrides,canonical(dimension.name)))consumedDimensions.add(canonical(dimension.name));
      evidence.push({feature:feature.name,...result.evidence});continue;
    }
    if(feature.type==='MirrorPattern'){
      const properties=feature.definition?.properties;
      if(!properties.Plane?.referencePlaneTransform||properties.secondaryMirrorDefinition)throw new DefinitionError('受驱焊件镜像未引用唯一原始固定基准面');
      const matrix=reflectionMatrix(properties.Plane),seeds=(properties.PatternFeatureArray??[]).map(seed=>featureReferenceMotions.get(seed.id)??featureReferenceMotions.get(seed.feature));
      if(seeds.some(motions=>!motions))throw new DefinitionError('焊件镜像原种子缺少受驱引用几何');const copies={planes:[],lines:[],cylinders:[]};
      for(const motions of seeds){const transformed=transformedMotions(motions,matrix,matrix);for(const kind of Object.keys(copies))copies[kind].push(...transformed[kind]);}
      addMotions(copies);registerFeatureMotions(feature,copies);continue;
    }
    const properties=feature.definition?.properties;if(!properties||![0,1,2,4,6].includes(properties.forwardEndCondition)||properties.directionReferences?.some(row=>row.reference)||properties.FromType!==0)throw new DefinitionError('受驱焊件切除起始或终止参考尚未求解');
    if(properties.forwardEndCondition===4){
      const reference=properties.forwardEndReference,plane=reference?.planeParameters;
      if(!Array.isArray(plane)||plane.length!==6||!plane.every(Number.isFinite))throw new DefinitionError('受驱焊件缺少原指定终止面：'+feature.name);
      const oldPoint=mm(plane.slice(3)),widthDelta=angleProfile?.pointDisplacement(oldPoint)??[0,0,0],normal=plane.slice(0,3),end=endIndex(oldPoint),lengthDelta=end<0?[0,0,0]:deltas[end];
      const displacement=widthDelta.map((value,index)=>value+lengthDelta[index]);
      targetConfiguration.features.find(row=>row.id===feature.id).definition.properties.forwardEndReference={...reference,planeParameters:[...normal,...meters(oldPoint.map((value,index)=>value+displacement[index]))]};
    }
    const profiles=configuration.features.filter(row=>row.sketch&&feature.parents.includes(row.name));if(profiles.length!==1)throw new DefinitionError('受驱焊件切除没有唯一原草图');
    const profile=profiles[0],matrix=swMatrix(profile.sketch.modelToSketch),toModel=inverseRigidMatrix(matrix);
    if(Math.abs(dot(toModel.slice(8,11),axis))>1e-8)throw new DefinitionError('受驱焊件切除草图未处于原始纵向平面');
    const localOverrides=Object.fromEntries(Object.entries(dimensionOverrides).filter(([name])=>name.split('@')[1]===profile.name));let sketch;
    const linear=Object.keys(localOverrides).length?null:linearSketchDrive(constraintConfiguration,feature),schematicCut=documentModel?driveSchematicBaseCut(template.source.path,profile,oldEnds,newEnds,toModel,matrix,localOverrides):null;
    if(schematicCut){sketch=schematicCut.sketch;evidence.push(schematicCut.evidence);(targetConfiguration.offlinePreviewApproximations??=[]).push(schematicCut.evidence);Object.keys(localOverrides).forEach(name=>consumedDimensions.add(name));}
    else if(profile.sketch.segments.every(row=>row.type===0)&&linear){
      const referencePoints={};
      for(const entity of profile.sketch.relations.flatMap(row=>row.entities).filter(row=>row.point&&!profile.sketch.points.some(point=>point.id===row.id))){
        const bound=resolveSmallOffsetAngleDependency(configuration,targetConfiguration,profile,entity);
        if(bound){referencePoints[entity.id]=bound.point;continue;}
        const asLocal=transformPoint(mm(entity.point),toModel),asModel=mm(entity.point),localIndex=endIndex(asLocal),modelIndex=endIndex(asModel);
        const coincidence=documentModel&&profile.sketch.relations.some(relation=>relation.type===9&&relation.entities.some(row=>row?.id===entity.id)&&relation.entities.some(row=>row?.id!==entity.id&&profile.sketch.points.some(point=>point.id===row?.id&&near(point.coordinates,entity.point,1e-9))));
        if(localIndex<0||localIndex!==modelIndex&&!coincidence)throw new DefinitionError('焊件外部点坐标归属不能唯一对应端平面');
        const widthDelta=angleProfile?.pointDisplacement(asLocal)??[0,0,0],delta=transformDirection(deltas[localIndex].map((value,index)=>value+widthDelta[index]),matrix);referencePoints[entity.id]=entity.point.map((value,index)=>value+delta[index]/1000);
      }
      sketch=linear.solve({},referencePoints).sketch;
    }else if(!Object.keys(localOverrides).length&&profile.sketch.segments.every(row=>row.type===1&&!row.construction&&near(row.geometry.start,row.geometry.end,1e-9))){
      const baseline=circularSketch(profile.sketch,reference=>({origin:mm(reference.lineParameters.slice(0,3))}),profile.parents.includes('原点'));
      if(baseline.points.some(point=>!near(point.coordinates,profile.sketch.points.find(row=>row.id===point.id).coordinates,1e-8)))throw new DefinitionError('圆孔约束基线未复现原始位置');
      sketch=circularSketch(profile.sketch,moveLine,profile.parents.includes('原点'));
    }else if(documentModel&&profile.sketch.segments.some(row=>row.type===1&&!near(row.geometry.start,row.geometry.end,1e-9))){
      const references={},redundantFaces=new Set(profile.sketch.relations.filter(relation=>redundantUnresolvedSlotFace(profile.sketch,relation)).flatMap(relation=>relation.entities.filter(entity=>entity?.definitionReference).map(entity=>entity.id)));
      for(const relation of profile.sketch.relations)for(const reference of relation.entities.filter(Boolean)){
        if(redundantFaces.has(reference.id))continue;
        if(profile.sketch.points.some(point=>point.id===reference.id)||profile.sketch.segments.some(segment=>segment.id===reference.id))continue;
        if(reference.start&&reference.end){
          if(!reference.lineParameters){const direction=subtract(reference.end,reference.start),length=norm(direction);reference.lineParameters=[...reference.start,...direction.map(value=>value/length)];}
          const moved=moveLine(reference),ends=[reference.start,reference.end].map(point=>{const index=endIndex(mm(point));return index<0?point:point.map((value,axis)=>value+deltas[index][axis]/1000);});
          references[reference.id]={...reference,start:moved.start?meters(moved.start):ends[0],end:moved.end?meters(moved.end):ends[1],lineParameters:[...meters(moved.origin),...reference.lineParameters.slice(3)]};
        }else if(reference.circleParameters){
          const center=mm(reference.circleParameters.slice(0,3)),matches=cylinderMotions.filter(row=>near(row.point,center)&&Math.abs(row.radius-reference.circleParameters[6]*1000)<1e-5),changes=matches.map(row=>subtract(row.newPoint,row.point));
          if(!changes.length||changes.some(delta=>!near(delta,changes[0])))throw new DefinitionError('腰孔引用圆弧没有唯一原种子');
          const delta=changes[0];references[reference.id]={...reference,circleParameters:[...meters(center.map((value,axis)=>value+delta[axis])),...reference.circleParameters.slice(3)],start:reference.start?.map((value,axis)=>value+delta[axis]/1000),end:reference.end?.map((value,axis)=>value+delta[axis]/1000)};
        }else{
          const coincidence=profile.sketch.relations.find(row=>row.type===9&&row.entities.some(entity=>entity?.id===reference.id)&&row.entities.some(entity=>profile.sketch.points.some(point=>point.id===entity?.id))),nativePoint=coincidence?.entities.find(entity=>profile.sketch.points.some(point=>point.id===entity?.id)),point=reference.point??nativePoint?.point;
          if(!point)throw new DefinitionError('腰孔外部点没有原重合证据',{path:template.source.path,profile:profile.name,reference});const index=endIndex(transformPoint(mm(point),toModel));
          if(index>=0){const delta=transformDirection(deltas[index],matrix);references[reference.id]={...reference,point:point.map((value,axis)=>value+delta[axis]/1000)};}
          else{
            const modelPoint=transformPoint(mm(point),toModel),matches=cylinderMotions.filter(row=>near(row.point,modelPoint)),changes=matches.map(row=>subtract(row.newPoint,row.point));
            if(changes.length&&changes.every(delta=>near(delta,changes[0]))){const delta=transformDirection(changes[0],matrix);references[reference.id]={...reference,point:point.map((value,axis)=>value+delta[axis]/1000)};}
            else if(!profile.parents.includes('原点')||!near(point,[0,0,0],1e-10))throw new DefinitionError('腰孔外部点没有原端部、原圆心或固定原点证据');
          }
        }
      }
      const result=driveRigidSlotSketch(profile.sketch,localOverrides,references);sketch=result.sketch;Object.keys(localOverrides).forEach(name=>consumedDimensions.add(name));evidence.push({feature:feature.name,...result.evidence});
    }else{
      // Consume the entire native axis-aligned system, including collinear
      // external edges and construction lines. A longitudinal reference is an
      // unchanged infinite line; a transverse reference must belong to one
      // uniquely identified original end plane.
      let constraintSource=constraintConfiguration;
      if(documentModel){
        const recovered=structuredClone(profile);
        for(const relation of recovered.sketch.relations.filter(row=>row.type===27&&row.entities.length===2)){
          const local=relation.entities.find(entity=>recovered.sketch.segments.some(segment=>segment.id===entity?.id)),external=relation.entities.find(entity=>entity&&!recovered.sketch.segments.some(segment=>segment.id===entity.id)&&!entity.start&&!entity.point&&!entity.referencePlaneTransform);
          if(!local||!external)continue;const segment=recovered.sketch.segments.find(row=>row.id===local.id),start=transformPoint(mm(segment.geometry.start),toModel),end=transformPoint(mm(segment.geometry.end),toModel),indices=[endIndex(start),endIndex(end)];
          if(segment.type!==0||indices[0]<0||indices[0]!==indices[1])throw new DefinitionError('缺少几何的原共线引用没有唯一原端面');
          const direction=subtract(end,start),length=norm(direction);Object.assign(external,{start:meters(start),end:meters(end),lineParameters:[...meters(start),...direction.map(value=>value/length)],positionEvidence:'native-collinearity-at-original-weld-end-plane'});
        }
        constraintSource={...constraintConfiguration,features:constraintConfiguration.features.map(feature=>feature.id===profile.id?recovered:feature)};
      }
      let solver;try{solver=axisSketchDrive(constraintSource,profile.name,{governingDimensions:Object.keys(localOverrides),preserveCapturedDirections:!!documentModel,retainFreeCoordinates:!!documentModel});}catch(error){error.source={...error.source,path:template.source.path,profile:profile.name};throw error;}const references={};
      for(const reference of solver.references){
        const bound=resolveSmallOffsetAngleDependency(configuration,targetConfiguration,profile,reference);
        if(bound){references[reference.id]=bound;continue;}
        if(reference.lineParameters&&reference.start&&reference.end){
          const moved=moveLine(reference),delta=subtract(moved.origin,mm(reference.lineParameters.slice(0,3)));
          references[reference.id]={...reference,start:reference.start.map((value,index)=>value+delta[index]/1000),end:reference.end.map((value,index)=>value+delta[index]/1000),lineParameters:[...meters(moved.origin),...reference.lineParameters.slice(3)]};
        }else if(reference.point){
          const index=endIndex(transformPoint(mm(reference.point),toModel));
          if(index>=0){const widthDelta=angleProfile?.pointDisplacement(transformPoint(mm(reference.point),toModel))??[0,0,0],delta=transformDirection(deltas[index].map((value,axis)=>value+widthDelta[axis]),matrix);references[reference.id]={...reference,point:reference.point.map((value,axis)=>value+delta[axis]/1000)};}
          else if(!profile.parents.includes('原点')||!near(reference.point,[0,0,0],1e-10))throw new DefinitionError('焊件后续约束点没有原端面或固定原点归属');
        }else throw new DefinitionError('焊件后续约束缺少可验证的原直线或点');
      }
      let solvedProfile;try{solvedProfile=solver.solve(localOverrides,references);}catch(error){error.source={...error.source,path:template.source.path,profile:profile.name};throw error;}sketch=solvedProfile.sketch;Object.keys(localOverrides).forEach(name=>consumedDimensions.add(name));if(solvedProfile.capturedFreeCoordinatePolicy)evidence.push({profile:profile.name,source:solvedProfile.capturedFreeCoordinatePolicy,freeCoordinates:solvedProfile.freeConstructionCoordinates});
    }
    const cylinders=[],planeStart=planeMotions.length,lineStart=lineMotions.length;
    for(const [segmentIndex,segment]of profile.sketch.segments.entries()){
      const changedSegment=sketch.segments[segmentIndex];
      if(segment.type===1)cylinders.push({point:transformPoint(mm(segment.geometry.center),toModel),newPoint:transformPoint(mm(changedSegment.geometry.center),toModel),axis:toModel.slice(8,11),radius:segment.geometry.radius*1000});
      else if(!segment.construction){
        lineMotions.push({start:transformPoint(mm(segment.geometry.start),toModel),end:transformPoint(mm(segment.geometry.end),toModel),newStart:transformPoint(mm(changedSegment.geometry.start),toModel),newEnd:transformPoint(mm(changedSegment.geometry.end),toModel)});
        const direction=transformDirection(subtract(mm(segment.geometry.end),mm(segment.geometry.start)),toModel),extrusionAxis=toModel.slice(8,11),normal=[direction[1]*extrusionAxis[2]-direction[2]*extrusionAxis[1],direction[2]*extrusionAxis[0]-direction[0]*extrusionAxis[2],direction[0]*extrusionAxis[1]-direction[1]*extrusionAxis[0]],magnitude=norm(normal);
        if(magnitude<1e-8)throw new DefinitionError('焊件切口侧面退化');planeMotions.push({point:transformPoint(mm(segment.geometry.start),toModel),newPoint:transformPoint(mm(changedSegment.geometry.start),toModel),normal:normal.map(value=>value/magnitude)});
      }
    }
    cylinderMotions.push(...cylinders);registerFeatureMotions(feature,{planes:planeMotions.slice(planeStart),lines:lineMotions.slice(lineStart),cylinders});
    targetConfiguration.features.find(row=>row.id===profile.id).sketch=sketch;evidence.push({feature:feature.name,sketch:profile.name,source:'captured-complete-sketch-constraints'});
  }
  if(documentModel)for(const name of Object.keys(dimensionOverrides)){
    let feature=configuration.features.find(row=>row.name===name.split('@')[1]);const visited=new Set();
    while(feature&&!visited.has(feature.id)){visited.add(feature.id);if(feature.suppressed){consumedDimensions.add(name);break;}feature=configuration.features.find(row=>row.name===feature.parent);}
  }
  if(documentModel){
    const dependencies=new Set(),visit=feature=>{if(!feature||dependencies.has(feature.name))return;dependencies.add(feature.name);for(const parent of feature.parents??[])visit(configuration.features.find(row=>row.name===parent));};geometry.forEach(visit);
    for(const name of Object.keys(dimensionOverrides))if(!dependencies.has(name.split('@')[1])){consumedDimensions.add(name);evidence.push({dimension:name,source:'native-dimension-without-active-geometric-dependent'});}
    // A complete graph also contains governed stock-profile dimensions that
    // remain exactly at the captured value. Their existing profile is retained;
    // changing one still requires an explicit geometric driver above.
    for(const name of Object.keys(dimensionOverrides))if(!consumedDimensions.has(name)&&Math.abs(dimensionOverrides[name]-captured[name])<1e-7){consumedDimensions.add(name);evidence.push({dimension:name,source:'unchanged-native-profile-dimension'});}
  }
  if((documentModel?Object.keys(dimensionOverrides):equationTargets).some(name=>!consumedDimensions.has(name)))throw new DefinitionError('焊件内部方程目标没有进入实际特征求解',{path:template.source.path,dimensions:(documentModel?Object.keys(dimensionOverrides):equationTargets).filter(name=>!consumedDimensions.has(name))});
  for(const feature of targetConfiguration.features){for(const dimension of feature.dimensions)if(Object.hasOwn(dimensionOverrides,canonical(dimension.name)))dimension.value=dimensionOverrides[canonical(dimension.name)]/(dimension.dimensionType===2?1:1000);for(const relation of feature.sketch?.relations??[])if(relation.dimension&&Object.hasOwn(dimensionOverrides,canonical(relation.dimension.name)))relation.dimension.value=dimensionOverrides[canonical(relation.dimension.name)]/1000;}
  if(documentModel)targetConfiguration.equations=[];
  return {template:derived,motion:{axis,planes:planeMotions,profilePlanes:angleProfile?.profilePlanes??[],cylinders:cylinderMotions,lines:lineMotions},evidence:{dimension:drive.dimension,sourceValue:drive.sourceValue,value:drive.value,path:path.name,localEquationDimensions:Object.fromEntries(equationTargets.map(name=>[name,solvedEquations[name]])),dependentFeatures:evidence,source:'source-straight-weld-constraints',nativeGeometryEquivalenceVerified:false}};
}

export function weldReferenceDisplacement(motion,point,direction,referenceType,radius){
  const unit=direction.map(value=>value/norm(direction)),alignment=Math.abs(dot(unit,motion.axis));let candidates;
  if(referenceType===3){
    if(alignment<1e-8){
      const matches=(motion.profilePlanes??[]).filter(row=>Math.abs(Math.abs(dot(unit,row.normal))-1)<1e-6&&Math.abs(dot(unit,subtract(point,row.point)))<=1).map(row=>unit.map(value=>value*dot(unit,subtract(row.newPoint,row.point))));
      if(!matches.length)return [0,0,0];
      if(matches.some(delta=>Math.hypot(...subtract(delta,matches[0]))>1))throw new DefinitionError('立角钢配合面对应了不同的截面位移');
      return matches[0];
    }
    // A plane mate consumes only normal motion. Two coplanar source features
    // can move tangentially by different amounts when the angle leg widens.
    candidates=motion.planes.filter(row=>Math.abs(Math.abs(dot(unit,row.normal))-1)<1e-6&&Math.abs(dot(unit,subtract(point,row.point)))<1e-4)
      .map(row=>unit.map(value=>value*dot(unit,subtract(row.newPoint,row.point))));
  }else if(referenceType===1){
    if(Math.abs(alignment-1)<1e-8)return [0,0,0];
    candidates=motion.planes.filter(row=>Math.abs(Math.abs(dot(motion.axis,row.normal))-1)<1e-6&&Math.abs(dot(row.normal,subtract(point,row.point)))<1e-4).map(row=>subtract(row.newPoint,row.point));
  }else if(referenceType===4){
    if(Math.abs(alignment-1)<1e-8)return [0,0,0];
    candidates=motion.cylinders.filter(row=>Math.abs(row.radius-radius)<1e-5&&Math.abs(Math.abs(dot(unit,row.axis))-1)<1e-6&&norm(subtract(subtract(point,row.point),unit.map(value=>value*dot(unit,subtract(point,row.point)))))<1e-4).map(row=>subtract(row.newPoint,row.point));
  }else throw new DefinitionError('受驱焊件配合实体类型尚未支持');
  if(!candidates.length||candidates.some(delta=>norm(subtract(delta,candidates[0]))>1))throw new DefinitionError('受驱焊件配合面或孔轴不能唯一对应原始特征',{candidateDisplacementsMm:candidates});
  return candidates[0];
}
