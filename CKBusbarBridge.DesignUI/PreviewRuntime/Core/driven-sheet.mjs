import {DefinitionError,parseExpression} from './expressions.mjs';
import {axisSketchDrive} from './axis-sketch-drive.mjs';
import {activeTemplateGeometry,swMatrix,inverseRigidMatrix,transformPoint} from './geometry.mjs';
import {transformDirection} from './template-planes.mjs';
import {driveLinearPattern} from './driven-patterns.mjs';
import {driveCompleteSketchRegions} from './driven-sketch-regions.mjs';
import {driveRigidSlotSketch} from './rigid-slot-drive.mjs';
const canonical=name=>name.split('@').slice(0,-1).join('@');
const add=(first,second)=>first.map((value,index)=>value+second[index]),sub=(first,second)=>first.map((value,index)=>value-second[index]);
const scale=(vector,value)=>vector.map(coordinate=>coordinate*value),dot=(first,second)=>first.reduce((sum,value,index)=>sum+value*second[index],0);
const norm=vector=>Math.hypot(...vector),unit=vector=>scale(vector,1/norm(vector));
const mm=point=>scale(point,1000),metres=point=>scale(point,.001);
const near=(first,second,tolerance=1e-6)=>norm(sub(first,second))<tolerance;
const toNative=matrix=>[matrix[0],matrix[1],matrix[2],matrix[4],matrix[5],matrix[6],matrix[8],matrix[9],matrix[10],...metres(matrix.slice(12,15)),1,0,0,0];
const one=(rows,label)=>{if(rows.length!==1)throw new DefinitionError(label+'未唯一对应');return rows[0];};
const equationRightSide=expression=>JSON.stringify(parseExpression(expression.slice(expression.indexOf('=')+1)));

// 032 cover dimensions are measurements of an origin-centred rectangle.
// Their active in-context point constraints carry the original frame spans.
function driveOffsetCoverProfile(profile,solver,inputs){
  const sketch=profile.sketch,points=new Map(sketch.points.map(point=>[point.id,point.coordinates])),
    segments=new Map(sketch.segments.map(segment=>[segment.id,segment])),references={},measurements=[];
  for(const [name,value] of Object.entries(inputs)){
    const relation=one(sketch.relations.filter(row=>row.dimension&&canonical(row.dimension.name)===name&&row.suppressed&&row.dimension.drivenState===1),'原盖板测量尺寸 '+name);
    const ids=relation.entities.map(entity=>entity.id),a=points.get(ids[0]),b=points.get(ids[1]),
      axes=a&&b?[0,1].filter(axis=>Math.abs(a[axis]-b[axis])*1000>1):[];
    if(ids.length!==2||axes.length!==1||!(value>0))throw new DefinitionError('盖板测量尺寸未对应原轴向边：'+name);
    const axis=axes[0],originalSpan=Math.abs(a[axis]-b[axis])*1000;
    if(Math.abs(a[axis]+b[axis])*1000>1)throw new DefinitionError('盖板尺寸未围绕原点对称：'+name);
    const candidates=sketch.relations.flatMap(row=>{
      if(row.suppressed||![9,25,26].includes(row.type)||row.entities.length!==2)return [];
      const external=row.entities.find(entity=>entity?.point&&!points.has(entity.id)),local=row.entities.find(entity=>entity!==external);
      if(!external||!local||![a[axis],b[axis]].some(station=>Math.abs(station-external.point[axis])*1000<=1))return [];
      if(row.type===9){
        const segment=segments.get(local.id);
        if(segment?.type!==0||Math.abs(segment.geometry.start[axis]-segment.geometry.end[axis])*1000>1)return [];
      }else if(!points.has(local.id)||(row.type===25?1:0)!==axis)return [];
      return [external];
    });
    const anchor=one(candidates,'原盖板外部定位点 '+name);
    if(Object.hasOwn(references,anchor.id))throw new DefinitionError('盖板外部定位点重复：'+name);
    const point=[...anchor.point];
    point[axis]+=Math.sign(point[axis])*(value-originalSpan)/2000;
    references[anchor.id]={...anchor,point};
    measurements.push({ids,axis,value,name});
  }
  const result=solver.solve({},references),updated=new Map(result.sketch.points.map(point=>[point.id,point.coordinates]));
  for(const {ids,axis,value,name} of measurements){
    if(Math.abs(Math.abs(updated.get(ids[0])[axis]-updated.get(ids[1])[axis])*1000-value)>1)
      throw new DefinitionError('盖板受驱轮廓与原测量尺寸不一致：'+name);
  }
  return result;
}

// The flange-end approximation is the user-approved corner policy. Base plate
// dimensions come from the complete native sketch system. Selected edges,
// sheet thickness, flange heights and original end trims remain explicit.
export function driveSheetDefinition(source,model){
  try{return driveSheet(source,model);}
  catch(error){
    if(!(error instanceof DefinitionError))throw error;
    const contextual=new DefinitionError(source.source.path+' ['+model.configuration+']：'+error.message,error.source);
    contextual.cause=error;throw contextual;
  }
}

function driveSheet(source,model){
  if(source.source.path!==model.templatePath)throw new DefinitionError('钣金驱动源身份不一致');
  const original=structuredClone(one(source.configurations.filter(row=>row.name===model.configuration),'钣金原配置'));
  for(const state of model.featureStates??[])one(original.features.filter(row=>row.name===state.feature),'原压缩目标').suppressed=state.suppressed;
  const geometry=activeTemplateGeometry(original);
  if(original.components.length||geometry[0]?.type!=='SMBaseFlange'||geometry.slice(1).some(row=>!['EdgeFlange','MirrorPattern','CornerFeat','Cut','LPattern','FormToolInstance'].includes(row.type)))throw new DefinitionError('钣金驱动尚未覆盖该完整特征链');
  const base=geometry[0],profile=one(original.features.filter(row=>row.sketch&&base.parents.includes(row.name)),'原钣金基体轮廓');
  if(profile.sketch.contours.some(row=>!row.closed)||profile.sketch.segments.some(row=>row.type!==0))throw new DefinitionError('钣金基体不是原闭合直线轮廓');
  const values=Object.fromEntries(model.dimensions.map(row=>[row.dimension,row.valueMm??row.value])),inputs=Object.fromEntries(Object.entries(values).filter(([name])=>name.split('@')[1]===profile.name));
  if(!Object.keys(inputs).length)throw new DefinitionError('钣金驱动缺少原基体草图尺寸');
  const solver=axisSketchDrive({...original,equations:[]},profile.name,{governingDimensions:Object.keys(inputs)}),
    offsetCover=/^模板\/基架模板\/小偏移基架\/(?:8GX\.017\.222|01\/8GX\.017\.227)\.SLDPRT$/.test(model.templatePath),
    solved=offsetCover?driveOffsetCoverProfile(profile,solver,inputs):solver.solve(inputs),
    template=structuredClone(source),configuration=template.configurations.find(row=>row.name===model.configuration),targetProfile=configuration.features.find(row=>row.id===profile.id);
  delete configuration.validation;targetProfile.sketch=solved.sketch;
  for(const state of model.featureStates??[])one(configuration.features.filter(row=>row.name===state.feature),'受驱压缩目标').suppressed=state.suppressed;
  const toModel=inverseRigidMatrix(swMatrix(profile.sketch.modelToSketch)),toSketch=swMatrix(profile.sketch.modelToSketch),boundary=profile.sketch.segments.filter(row=>!row.construction).map(segment=>({old:segment.geometry,new:one(solved.sketch.segments.filter(row=>row.id===segment.id),'受驱原线').geometry})),planeMotions=[],cylinderMotions=[],lineMotions=[],louverDistribution=new Map(),cutDistribution=new Map();
  function lineDisplacement(point,{cornerInsetMm=base.definition.properties.BendRadius*1000+base.definition.properties.Thickness*1000}={}){
    const exact=[];for(const row of lineMotions){const direction=sub(row.end,row.start),length=dot(direction,direction),station=dot(sub(point,row.start),direction)/length,projection=add(row.start,scale(direction,station));if(station< -1e-7||station>1+1e-7||!near(point,projection))continue;exact.push(sub(add(row.newStart,scale(sub(row.newEnd,row.newStart),station)),projection));}
    if(exact.length){if(exact.some(delta=>!near(delta,exact[0])))throw new DefinitionError('钣金引用对应多个不同位移的原切口边');return exact[0];}
    const local=transformPoint(point,toSketch),matches=[];
    for(const pair of boundary){const a=mm(pair.old.start),b=mm(pair.old.end),direction=sub(b,a),span=dot(direction,direction),station=dot(sub(local,a),direction)/span,projection=add(a,scale(direction,station));if(Math.hypot(local[0]-projection[0],local[1]-projection[1])>1e-5||station< -1e-7||station>1+1e-7)continue;const changed=add(mm(pair.new.start),scale(sub(mm(pair.new.end),mm(pair.new.start)),station));matches.push(transformDirection(sub(changed,projection),toModel));}
    if(!matches.length&&cornerInsetMm>0){
      const changes=new Map();
      for(const pair of boundary){const oldA=mm(pair.old.start),oldB=mm(pair.old.end),axis=[0,1].find(axis=>Math.abs(oldA[axis]-oldB[axis])<1e-6);if(axis===undefined||Math.abs(Math.abs(local[axis]-oldA[axis])-cornerInsetMm)>1e-5)continue;const delta=(pair.new.start[axis]-pair.old.start[axis])*1000;if(changes.has(axis)&&Math.abs(changes.get(axis)-delta)>1e-6)throw new DefinitionError('法兰角部内缩边有冲突的原边界');changes.set(axis,delta);}
      if(changes.size===2)return transformDirection([changes.get(0),changes.get(1),0],toModel);
    }
    if(!matches.length||matches.some(delta=>!near(delta,matches[0])))throw new DefinitionError('钣金引用没有对应原基体边界',{path:source.source.path,point,local});
    return matches[0];
  }
  function addPlanes(sketch,updated,faceOffsets=[0]){
    const placement=inverseRigidMatrix(swMatrix(sketch.modelToSketch)),newPlacement=inverseRigidMatrix(swMatrix(updated.modelToSketch));
    for(const offset of faceOffsets)planeMotions.push({point:add(placement.slice(12,15),scale(placement.slice(8,11),offset)),normal:placement.slice(8,11),delta:sub(newPlacement.slice(12,15),placement.slice(12,15))});
    for(const segment of sketch.segments.filter(row=>row.type===1&&!row.construction)){const changed=one(updated.segments.filter(row=>row.id===segment.id),'受驱圆弧');cylinderMotions.push({point:transformPoint(mm(segment.geometry.center),placement),newPoint:transformPoint(mm(changed.geometry.center),newPlacement),radius:segment.geometry.radius*1000,newRadius:changed.geometry.radius*1000});}
    for(const segment of sketch.segments.filter(row=>row.type===0&&!row.construction)){
      const changed=one(updated.segments.filter(row=>row.id===segment.id),'受驱轮廓边'),a=transformPoint(mm(segment.geometry.start),placement),b=transformPoint(mm(segment.geometry.end),placement),newA=transformPoint(mm(changed.geometry.start),newPlacement),newB=transformPoint(mm(changed.geometry.end),newPlacement),oldDirection=unit(sub(b,a)),newDirection=unit(sub(newB,newA));
      lineMotions.push({start:a,end:b,newStart:newA,newEnd:newB});
      if(!near(oldDirection,newDirection))throw new DefinitionError('钣金边界驱动改变了原始方向');
      // The sketch-space normal is exact for the base plane; flange boundary
      // planes below use the cross product in model coordinates instead.
      const z=placement.slice(8,11),n=[oldDirection[1]*z[2]-oldDirection[2]*z[1],oldDirection[2]*z[0]-oldDirection[0]*z[2],oldDirection[0]*z[1]-oldDirection[1]*z[0]];
      planeMotions.push({point:a,normal:unit(n),delta:sub(newA,a)});
    }
  }
  const baseThickness=base.definition.properties.Thickness*1000,baseLower=base.definition.properties.SymmetricThickness?-baseThickness/2:base.definition.properties.ReverseThickness?0:-baseThickness;
  addPlanes(profile.sketch,solved.sketch,[baseLower,baseLower+baseThickness]);
  function planeDisplacement(point,normal){
    const matches=planeMotions.filter(row=>Math.abs(Math.abs(dot(row.normal,unit(normal)))-1)<1e-7&&Math.abs(dot(row.normal,sub(point,row.point)))<1e-5).map(row=>scale(unit(normal),dot(unit(normal),row.delta)));
    if(!matches.length||matches.some(delta=>!near(delta,matches[0])))throw new DefinitionError('钣金配合平面没有唯一受驱边界');
    return matches[0];
  }
  const consumed=new Set(Object.keys(inputs)),evidence=[{source:'complete-native-base-sketch',profile:profile.name,dimensions:inputs,equationCount:solved.equationCount}];
  for(const feature of geometry.slice(1)){
    if(feature.type==='LPattern'){
      const properties=feature.definition.properties,singleInstanceDirections={};for(const index of [1,2]){const reference=properties['D'+index+'Axis'];if(reference?.start&&reference.end)singleInstanceDirections[index]=scale(unit(sub(reference.end,reference.start)),properties['D'+index+'ReverseDirection']?-1:1);}
      if(!properties.D1Axis){const spacing=model.dimensions.find(row=>row.dimension==='D3@'+feature.name),assignments=spacing?.equations.flatMap(row=>[/^\s*"[^"]+"\s*=\s*"([^"]+)"\s*$/.exec(row.sourceEquation)?.[1],equationRightSide(row.sourceEquation)]).filter(Boolean)??[],directions=assignments.flatMap(name=>louverDistribution.has(name)?[louverDistribution.get(name)]:[]);if(directions.length&&directions.every(direction=>near(direction,directions[0])))singleInstanceDirections[1]=directions[0];}
      if(!properties.D1Axis&&properties.D1TotalInstances===1&&!singleInstanceDirections[1]){const directions=(properties.PatternFeatureArray??[]).flatMap(seed=>cutDistribution.get(seed.id)??cutDistribution.get(seed.feature)??[]);if(directions.length&&directions.every(direction=>near(direction,directions[0]))){singleInstanceDirections[1]=directions[0];evidence.push({feature:feature.name,source:'suppressed-native-pattern-direction-from-driven-seed-distance'});}}
      // The 12 captured two-direction grids of these closed base-flange parts
      // all enumerate D2 inside D1. Their 55 measurable edge axes agree with
      // the selected edge vertices and native reversal flag.
      const driven=driveLinearPattern(feature,values,{singleInstanceDirections,nativeInnerAxis:2});configuration.features[configuration.features.findIndex(row=>row.id===feature.id)]=driven.feature;for(const dimension of feature.dimensions)if(Object.hasOwn(values,canonical(dimension.name)))consumed.add(canonical(dimension.name));evidence.push({feature:feature.name,...driven.evidence});continue;
    }
    if(feature.type==='FormToolInstance'){
      const footprint=one(original.features.filter(row=>row.id===feature.schematicDefinition?.footprintSketchId),'原百叶窗轮廓'),placement=structuredClone(one(original.features.filter(row=>row.sketch&&feature.parents.includes(row.name)&&footprint.parents.includes(row.name)),'原百叶窗定位草图')),origin=inverseRigidMatrix(swMatrix(footprint.sketch.modelToSketch)).slice(12,15),placementToModel=inverseRigidMatrix(swMatrix(placement.sketch.modelToSketch)),anchors=placement.sketch.points.filter(point=>near(transformPoint(mm(point.coordinates),placementToModel),origin)),localValues=Object.fromEntries(Object.entries(values).filter(([name])=>name.split('@')[1]===placement.name));
      for(const relation of placement.sketch.relations.filter(row=>row.type===1&&row.dimension)){
        const missing=relation.entities.find(entity=>entity&&!entity.point&&!entity.start&&!placement.sketch.segments.some(segment=>segment.id===entity.id));if(!missing)continue;const local=relation.entities.find(entity=>entity.id!==missing.id),segment=placement.sketch.segments.find(segment=>segment.id===local?.id);if(segment?.type!==0)continue;const axis=[0,1].find(axis=>Math.abs(segment.geometry.start[1-axis]-segment.geometry.end[1-axis])<1e-8);if(axis===undefined)continue;
        const matrix=swMatrix(placement.sketch.modelToSketch),candidates=lineMotions.filter(row=>{const a=transformPoint(row.start,matrix),b=transformPoint(row.end,matrix);return Math.abs(a[1-axis]-b[1-axis])<1e-5&&Math.abs(Math.abs(a[1-axis]-segment.geometry.start[1-axis]*1000)-relation.dimension.value*1000)<1e-5;});if(!candidates.length||new Set(candidates.map(row=>transformPoint(row.start,matrix)[1-axis].toFixed(6))).size!==1)continue;const boundary=candidates[0];Object.assign(missing,{start:metres(boundary.start),end:metres(boundary.end),lineParameters:[...metres(boundary.start),...unit(sub(boundary.end,boundary.start))],positionEvidence:'native-louver-distance-to-unique-original-profile-boundary'});
      }
      if(!anchors.length)throw new DefinitionError('百叶窗原定位点未对应轮廓原点');
      const solver=axisSketchDrive({...original,equations:[],features:original.features.map(row=>row.id===placement.id?placement:row)},placement.name,{governingDimensions:Object.keys(localValues),protectedPointIds:anchors.map(point=>point.id)}),references={};
      for(const reference of solver.references){if(!reference.start||!reference.end)throw new DefinitionError('百叶窗定位缺少原边界');const start=mm(reference.start),end=mm(reference.end),newStart=add(start,lineDisplacement(start)),newEnd=add(end,lineDisplacement(end));references[reference.id]={...reference,start:metres(newStart),end:metres(newEnd),lineParameters:[...metres(newStart),...unit(sub(newEnd,newStart))]};}
      const result=solver.solve(localValues,references),deltas=anchors.map(point=>sub(transformPoint(mm(result.sketch.points.find(row=>row.id===point.id).coordinates),placementToModel),origin));
      for(const relation of placement.sketch.relations.filter(row=>row.dimension&&Object.hasOwn(localValues,canonical(row.dimension.name)))){
        const reference=relation.entities.find(entity=>entity?.lineParameters);if(!reference)continue;const line=reference.lineParameters,direction=unit(line.slice(3,6)),basePoint=mm(line.slice(0,3)),delta=sub(origin,basePoint),across=sub(delta,scale(direction,dot(direction,delta)));
        if(norm(across)>1e-6){const name=canonical(relation.dimension.name),direction=unit(across);louverDistribution.set(name,direction);for(const equation of model.dimensions.find(row=>row.dimension===name)?.equations??[]){const key=equationRightSide(equation.sourceEquation),previous=louverDistribution.get(key);if(previous&&!near(previous,direction))throw new DefinitionError('同一原百叶窗定位表达式对应不同分布方向');louverDistribution.set(key,direction);}}
      }
      if(deltas.some(delta=>!near(delta,deltas[0])))throw new DefinitionError('百叶窗原定位点驱动后分离');configuration.features.find(row=>row.id===placement.id).sketch=result.sketch;
      const target=configuration.features.find(row=>row.id===footprint.id),matrix=inverseRigidMatrix(swMatrix(footprint.sketch.modelToSketch));for(let axis=0;axis<3;axis++)matrix[12+axis]+=deltas[0][axis];target.sketch.modelToSketch=toNative(inverseRigidMatrix(matrix));Object.keys(localValues).forEach(name=>consumed.add(name));evidence.push({feature:feature.name,source:'native-louver-placement-point',movementMm:deltas[0],shapePolicy:'user-approved-louver-schematic'});continue;
    }
    if(feature.type==='Cut'){
      const candidates=original.features.filter(row=>row.sketch&&feature.parents.includes(row.name)),profiles=candidates.filter(candidate=>!candidates.some(other=>other!==candidate&&other.parents.includes(candidate.name))),cutProfile=one(profiles,'原切口轮廓');
      const recoveredProfile=structuredClone(cutProfile),cutMatrix=swMatrix(cutProfile.sketch.modelToSketch),cutToModel=inverseRigidMatrix(cutMatrix),dependentReferences={};
      const lineReference=(entity,[start,end])=>({...entity,start:metres(start),end:metres(end),lineParameters:[...metres(start),...unit(sub(end,start))]});
      for(const entity of recoveredProfile.sketch.relations.flatMap(row=>row.entities).filter(Boolean)){
        if(recoveredProfile.sketch.points.some(point=>point.id===entity.id))continue;
        const captured=entity.sourceModelEdge,profileEdgeId=captured?.segmentId??entity.id,
          originalEdge=profile.sketch.segments.find(segment=>segment.id===profileEdgeId&&!segment.construction&&segment.type===0);
        if(captured||originalEdge){
          if(!originalEdge||!feature.parents.includes(base.name)||captured&&
            (captured.featureId!==base.id||captured.profileId!==profile.id))throw new DefinitionError(cutProfile.name+'：外部模型边未对应原法兰及驱动轮廓');
          const drivenEdge=one(solved.sketch.segments.filter(segment=>segment.id===profileEdgeId),'受驱原轮廓边'),
            edgePoints=edge=>[edge.geometry.start,edge.geometry.end].map(point=>transformPoint(mm(point),toModel)),
            originalPoints=edgePoints(originalEdge),drivenPoints=edgePoints(drivenEdge);
          let sourcePoints=originalPoints;
          if(captured){
            const endpoints=captured.sketchEndpoints;
            if(!Array.isArray(endpoints)||endpoints.length!==2||endpoints.some(point=>!Array.isArray(point)||point.length!==3||!point.every(Number.isFinite)))
              throw new DefinitionError(cutProfile.name+'：外部模型边缺少原草图两端点');
            const projected=originalPoints.map(point=>transformPoint(point,cutMatrix)),
              matches=(point,index)=>Math.hypot(point[0]*1000-projected[index][0],point[1]*1000-projected[index][1])<=1;
            const forward=endpoints.every(matches),reverse=endpoints.every((point,index)=>matches(point,1-index));
            if(forward===reverse)throw new DefinitionError(cutProfile.name+'：外部模型边端点与原驱动轮廓不唯一');
            sourcePoints=(forward?endpoints:[...endpoints].reverse()).map(point=>transformPoint(mm(point),cutToModel));
          }
          Object.assign(entity,lineReference(entity,sourcePoints));
          dependentReferences[entity.id]=lineReference(entity,sourcePoints.map((point,index)=>add(point,sub(drivenPoints[index],originalPoints[index]))));
          continue;
        }
        const owners=original.features.filter(row=>row.id!==cutProfile.id&&row.sketch?.points.some(point=>point.id===entity.id));if(owners.length!==1)continue;
        const owner=owners[0],updatedOwner=configuration.features.find(row=>row.id===owner.id),sourcePoint=owner.sketch.points.find(row=>row.id===entity.id),updatedPoint=updatedOwner.sketch.points.find(row=>row.id===entity.id),project=(point,sketch)=>transformPoint(transformPoint(mm(point.coordinates),inverseRigidMatrix(swMatrix(sketch.modelToSketch))),cutMatrix).map((value,axis)=>axis===2?0:value/1000);
        entity.point=project(sourcePoint,owner.sketch);entity.positionEvidence='native-point-identity-in-original-parent-sketch';dependentReferences[entity.id]={...entity,point:project(updatedPoint,updatedOwner.sketch)};
      }
      for(const relation of recoveredProfile.sketch.relations.filter(row=>!row.suppressed&&row.type===1&&row.dimension)){
        const missing=relation.entities.filter(entity=>entity&&!entity.point&&!entity.start&&!entity.circleParameters&&!recoveredProfile.sketch.segments.some(segment=>segment.id===entity.id));
        if(!missing.length)continue;const local=relation.entities.find(entity=>recoveredProfile.sketch.segments.some(segment=>segment.id===entity?.id)),segment=recoveredProfile.sketch.segments.find(row=>row.id===local?.id);
        if(segment?.type!==0)continue;const axis=[0,1].find(axis=>Math.abs(segment.geometry.start[1-axis]-segment.geometry.end[1-axis])<1e-8);if(axis===undefined)continue;
        const candidates=lineMotions.filter(row=>{const a=transformPoint(row.start,cutMatrix),b=transformPoint(row.end,cutMatrix);return Math.abs(a[1-axis]-b[1-axis])<1e-5&&Math.abs(Math.abs(a[1-axis]-segment.geometry.start[1-axis]*1000)-relation.dimension.value*1000)<1e-5;});
        const stations=[...new Set(candidates.map(row=>transformPoint(row.start,cutMatrix)[1-axis].toFixed(6)))];
        if(stations.length!==1)continue;const boundary=candidates[0];for(const entity of missing){const direction=unit(sub(boundary.end,boundary.start));Object.assign(entity,{start:metres(boundary.start),end:metres(boundary.end),lineParameters:[...metres(boundary.start),...direction],positionEvidence:'native-distance-to-unique-original-profile-boundary'});}
        evidence.push({profile:cutProfile.name,dimension:canonical(relation.dimension.name),source:'native-distance-to-unique-original-profile-boundary'});
      }
      const drivingProfile=recoveredProfile,localValues=Object.fromEntries(Object.entries(values).filter(([name])=>name.split('@')[1]===cutProfile.name)),capsules=drivingProfile.sketch.segments.some(row=>!row.construction&&row.type===1&&!near(row.geometry.start,row.geometry.end,1e-9)),regularPolygons=drivingProfile.sketch.relations.some(row=>!row.suppressed&&row.type===57),cutSolver=capsules||regularPolygons?null:axisSketchDrive({...original,equations:[],features:original.features.map(row=>row.id===cutProfile.id?drivingProfile:row)},cutProfile.name,{governingDimensions:Object.keys(localValues)}),references={...dependentReferences},nativeReferences=cutSolver?.references??drivingProfile.sketch.relations.flatMap(row=>row.entities).filter(entity=>entity&&!drivingProfile.sketch.points.some(point=>point.id===entity.id)&&!drivingProfile.sketch.segments.some(segment=>segment.id===entity.id));
      for(const reference of nativeReferences){
        if(dependentReferences[reference.id])continue;
        if(!reference.circleParameters&&reference.start&&reference.end){
          const start=mm(reference.start),end=mm(reference.end),newStart=add(start,lineDisplacement(start)),newEnd=add(end,lineDisplacement(end));
          references[reference.id]={...reference,start:metres(newStart),end:metres(newEnd),lineParameters:[...metres(newStart),...unit(sub(newEnd,newStart))]};
        }else if(reference.circleParameters){
          const point=mm(reference.circleParameters.slice(0,3)),matches=cylinderMotions.filter(row=>near(row.point,point)&&Math.abs(row.radius-reference.circleParameters[6]*1000)<1e-5),deltas=matches.map(row=>sub(row.newPoint,row.point));
          if(!deltas.length||deltas.some(delta=>!near(delta,deltas[0])))throw new DefinitionError('钣金切口引用圆弧没有唯一原种子');references[reference.id]={...reference,circleParameters:[...metres(add(point,deltas[0])),...reference.circleParameters.slice(3,6),matches[0].newRadius/1000],start:reference.start?.map((value,axis)=>value+deltas[0][axis]/1000),end:reference.end?.map((value,axis)=>value+deltas[0][axis]/1000)};
        }else if(reference.point){if(!near(reference.point,[0,0,0],1e-9)||!cutProfile.parents.includes('原点'))throw new DefinitionError('钣金切口定位点没有原固定原点证据');}
        else throw new DefinitionError(cutProfile.name+'：钣金切口定位缺少原始直边或原点',{path:source.source.path,configuration:model.configuration,profile:cutProfile.name,reference});
      }
      const result=capsules||regularPolygons?driveRigidSlotSketch(drivingProfile.sketch,localValues,references,{regularPolygons}):cutSolver.solve(localValues,references),target=configuration.features.find(row=>row.id===cutProfile.id);target.sketch=result.sketch;
      const distribution=[];
      for(const relation of drivingProfile.sketch.relations.filter(row=>row.type===1&&row.dimension&&model.dimensions.some(dimension=>dimension.dimension===canonical(row.dimension.name)&&dimension.equations.length&&Math.abs((dimension.valueMm??dimension.value)-(dimension.sourceValueMm??dimension.sourceValue))>1e-7))){
        const references=relation.entities.flatMap(entity=>{if(entity?.lineParameters&&!drivingProfile.sketch.segments.some(segment=>segment.id===entity.id))return [{reference:entity,entity}];const collinear=drivingProfile.sketch.relations.find(row=>row.type===27&&row.entities.some(candidate=>candidate?.id===entity?.id)&&row.entities.some(candidate=>candidate?.lineParameters&&!drivingProfile.sketch.segments.some(segment=>segment.id===candidate.id)));const reference=collinear?.entities.find(candidate=>candidate?.lineParameters);return reference?[{reference,entity}]:[];});if(references.length!==1)continue;const {reference,entity:referenceEntity}=references[0],local=relation.entities.find(entity=>entity?.id!==referenceEntity.id);if(!local)continue;
        const segment=drivingProfile.sketch.segments.find(segment=>segment.id===local.id),point=drivingProfile.sketch.points.find(point=>point.id===local.id)?.coordinates??segment?.geometry.start;if(!point)continue;
        const placement=inverseRigidMatrix(swMatrix(drivingProfile.sketch.modelToSketch)),position=transformPoint(mm(point),placement),basePoint=mm(reference.lineParameters.slice(0,3)),direction=unit(reference.lineParameters.slice(3,6)),delta=sub(position,basePoint),across=sub(delta,scale(direction,dot(delta,direction)));if(norm(across)>1e-6)distribution.push(unit(across));
      }
      cutDistribution.set(feature.id,distribution);cutDistribution.set(feature.name,distribution);
      const properties=configuration.features.find(row=>row.id===feature.id).definition?.properties;
      if(properties?.Contours?.length)properties.Contours=driveCompleteSketchRegions(cutProfile.sketch,target.sketch,properties.Contours).selections;
      for(const name of Object.keys(localValues))consumed.add(name);addPlanes(cutProfile.sketch,target.sketch);evidence.push({feature:feature.name,profile:cutProfile.name,source:'native-cut-sketch-position-and-dimension-system'});continue;
    }
    if(feature.type==='CornerFeat')continue;
    if(feature.type==='MirrorPattern'){
      const plane=feature.definition.properties.Plane;
      if(!plane?.referencePlaneTransform||!original.features.some(row=>row.name===plane.feature&&row.type==='RefPlane'&&row.definition?.properties.Type===10))throw new DefinitionError('钣金镜像没有原固定基准面');
      continue;
    }
    const properties=feature.definition.properties,updated=configuration.features.find(row=>row.id===feature.id).definition.properties;
    const edges=properties.Edges.map(edge=>{
      const start=mm(edge.start),end=mm(edge.end),inset={cornerInsetMm:properties.BendRadius*1000+baseThickness},newStart=add(start,lineDisplacement(start,inset)),newEnd=add(end,lineDisplacement(end,inset)),result=structuredClone(edge),delta=sub(newStart,start);
      result.start=metres(newStart);result.end=metres(newEnd);result.lineParameters=[...metres(add(mm(edge.lineParameters.slice(0,3)),delta)),...edge.lineParameters.slice(3,6)];
      for(const face of result.adjacentFaces){const normal=face.planeParameters.slice(0,3),point=mm(face.planeParameters.slice(3,6));face.planeParameters=[...normal,...metres(add(point,planeDisplacement(point,normal)))];}
      return {source:edge,updated:result};
    });
    updated.Edges=edges.map(row=>row.updated);if(properties.Edge)updated.Edge=one(edges.filter(row=>row.source.id===properties.Edge.id),'原法兰首选边').updated;
    for(const flange of original.features.filter(row=>row.parent===feature.name&&row.sketch&&!row.suppressed)){
      const sketch=flange.sketch,placement=inverseRigidMatrix(swMatrix(sketch.modelToSketch)),origin=placement.slice(12,15),along=placement.slice(0,3),normal=placement.slice(8,11),edge=one(edges.filter(row=>[row.source.start,row.source.end].every(point=>Math.abs(dot(sub(mm(point),origin),normal))<1e-5)&&Math.abs(Math.abs(dot(unit(sub(row.source.end,row.source.start)),along))-1)<1e-7),'原法兰轮廓选择边');
      if(sketch.segments.some(row=>row.type!==0||row.construction))throw new DefinitionError('法兰角部示意目前需要完整直线轮廓');
      const sourceRange=[edge.source.start,edge.source.end].map(point=>dot(sub(mm(point),origin),along)).sort((a,b)=>a-b),midpoint=scale(add(mm(edge.source.start),mm(edge.source.end)),.5),newMidpoint=scale(add(mm(edge.updated.start),mm(edge.updated.end)),.5),shift=sub(newMidpoint,midpoint),newOrigin=add(origin,shift),newRange=[edge.updated.start,edge.updated.end].map(point=>dot(sub(mm(point),newOrigin),along)).sort((a,b)=>a-b),stations=sketch.segments.flatMap(row=>[row.geometry.start[0]*1000,row.geometry.end[0]*1000]),lower=Math.min(...stations),upper=Math.max(...stations);
      const updatedSketch=structuredClone(sketch),map=point=>{const station=point[0]*1000;if(Math.abs(station-lower)>1e-5&&Math.abs(station-upper)>1e-5)throw new DefinitionError('法兰示意含未对应原端部的中间轮廓');return [point[0]+((Math.abs(station-lower)<1e-5?newRange[0]-sourceRange[0]:newRange[1]-sourceRange[1]))/1000,point[1],point[2]];};
      for(const segment of updatedSketch.segments)for(const name of ['start','end'])segment.geometry[name]=map(segment.geometry[name]);
      for(const point of updatedSketch.points)if(Math.abs(point.coordinates[0]*1000-lower)<1e-5||Math.abs(point.coordinates[0]*1000-upper)<1e-5)point.coordinates=map(point.coordinates);
      const moved=[...placement];for(let axis=0;axis<3;axis++)moved[12+axis]+=shift[axis];updatedSketch.modelToSketch=toNative(inverseRigidMatrix(moved));configuration.features.find(row=>row.id===flange.id).sketch=updatedSketch;addPlanes(sketch,updatedSketch);
      evidence.push({source:'native-flange-profile-with-preserved-end-trim',feature:feature.name,profile:flange.name,trimMm:[lower-sourceRange[0],sourceRange[1]-upper],cornerTreatment:'user-approved-schematic'});
    }
  }
  for(const name of Object.keys(values)){let feature=original.features.find(row=>row.name===name.split('@')[1]);const visited=new Set();while(feature&&!visited.has(feature.id)){visited.add(feature.id);if(feature.suppressed){consumed.add(name);break;}feature=original.features.find(row=>row.name===feature.parent);}}
  const dependencies=new Set(),visit=feature=>{if(!feature||dependencies.has(feature.name))return;dependencies.add(feature.name);for(const parent of feature.parents??[])visit(original.features.find(row=>row.name===parent));};geometry.forEach(visit);
  for(const name of Object.keys(values))if(!dependencies.has(name.split('@')[1])){consumed.add(name);evidence.push({dimension:name,source:'native-dimension-without-active-geometric-dependent'});}
  if(Object.keys(values).some(name=>!consumed.has(name)))throw new DefinitionError('钣金驱动包含尚未处理的后续尺寸：'+Object.keys(values).filter(name=>!consumed.has(name)).join('、'));
  for(const feature of configuration.features)for(const dimension of feature.dimensions){const name=canonical(dimension.name);if(Object.hasOwn(values,name))dimension.value=values[name]/(dimension.dimensionType===2?1:1000);}
  configuration.equations=[];
  return {template,evidence,referenceDisplacement:entity=>{if(entity.referenceType!==3)throw new DefinitionError('该受驱钣金配合尚非原平面');const reference=entity.reference;if((reference?.referencePlaneTransform||reference?.fixedPrincipalPlane)&&original.features.some(row=>row.name===reference.feature&&(!reference.fixedPrincipalPlane||row.id===reference.featureId)&&row.type==='RefPlane'&&row.definition?.properties.Type===10))return [0,0,0];if(!reference?.planeParameters)throw new DefinitionError('受驱钣金配合缺少原平面');return planeDisplacement(mm(reference.planeParameters.slice(3,6)),reference.planeParameters.slice(0,3));}};
}
