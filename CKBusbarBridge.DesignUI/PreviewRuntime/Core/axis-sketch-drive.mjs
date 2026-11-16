import {DefinitionError} from './expressions.mjs';
import {solveAffineSketchSystem} from './affine-sketch-system.mjs';

const canonical=name=>name.split('@').slice(0,-1).join('@');
const close=(a,b,tolerance=1e-7)=>a.length===b.length&&a.every((value,index)=>Math.abs(value-b[index])<tolerance);
const distance=(a,b)=>Math.hypot(...a.map((value,index)=>value-b[index]));
const project=(point,matrix)=>[0,1,2].map(axis=>point[0]*matrix[axis]+point[1]*matrix[3+axis]+point[2]*matrix[6+axis]+matrix[9+axis]);
const projectDirection=(direction,matrix)=>[0,1,2].map(axis=>direction[0]*matrix[axis]+direction[1]*matrix[3+axis]+direction[2]*matrix[6+axis]);

// Complete linear constraints for axis-aligned profiles and full circular holes.
// Every active native relation is consumed. External geometry is identified by
// its captured entity ID and may only change through an explicit reference map.
export function axisSketchDrive(configuration,profileName,{governingDimensions=[],protectedPointIds=[],preserveCapturedDirections=false,retainFreeCoordinates=false}={}){
  const profile=configuration.features.find(feature=>feature.name===profileName),sketch=profile?.sketch?structuredClone(profile.sketch):null;
  if(!sketch?.points?.length||sketch.points.length>256)throw new DefinitionError('二维约束草图缺少原始点：'+profileName);
  const pointMap=new Map(),lineMap=new Map(),circleMap=new Map(),externalMap=new Map(),dimensionRows=[],rows=[],directions=new Map(),endpointAliases=[];
  const fail=message=>{throw new DefinitionError(profileName+'：'+message);};
  for(const point of sketch.points){if(!point.id||pointMap.has(point.id)||!point.coordinates?.every(Number.isFinite)||Math.abs(point.coordinates[2])>1e-7)fail('草图点身份或平面坐标无效');pointMap.set(point.id,{coordinates:[...point.coordinates],index:pointMap.size});}
  // A native coincidence supplies an external anchor's captured position even
  // when the collector stored only its identity. It remains a fixed dependency
  // until the caller supplies an update for that exact external identity.
  const coincidentReferences=new Map();
  for(const relation of sketch.relations.filter(row=>!row.suppressed&&row.type===9&&row.entities.length===2)){
    const local=relation.entities.find(entity=>pointMap.has(entity.id)),external=relation.entities.find(entity=>!pointMap.has(entity.id)&&entity.referenceOnly);
    if(local&&external){const coordinates=pointMap.get(local.id).coordinates,previous=coincidentReferences.get(external.id);if(previous&&!close(previous,coordinates))fail('同一外部重合点的位置捕获冲突');coincidentReferences.set(external.id,coordinates);}
  }
  for(const relation of sketch.relations)for(const entity of relation.entities)if(entity.referenceOnly&&coincidentReferences.has(entity.id)){entity.point=[...coincidentReferences.get(entity.id)];entity.positionEvidence='native-coincidence-with-captured-sketch-point';}
  const pointId=coordinates=>{const matches=[...pointMap].filter(([,point])=>close(point.coordinates,coordinates));if(!matches.length)fail('端点或圆心没有原始点');if(matches.length>1)endpointAliases.push(matches.map(([id])=>id));return matches[0][0];};
  for(const segment of sketch.segments){
    if(segment.type===0)lineMap.set(segment.id,{points:[pointId(segment.geometry.start),pointId(segment.geometry.end)],segment});
    else if(segment.type===1&&close(segment.geometry.start,segment.geometry.end)&&Math.abs(Math.abs(segment.geometry.normal?.[2])-1)<1e-7)circleMap.set(segment.id,{center:pointId(segment.geometry.center),radius:segment.geometry.radius,index:circleMap.size,segment});
    else fail('只接受已捕获的直线与完整圆，不以圆替代圆弧');
  }
  const relations=sketch.relations.filter(relation=>!relation.suppressed),variableCount=pointMap.size*2+circleMap.size;
  const position=(id,axis)=>pointMap.get(id).coordinates[axis]*1000;
  function add(terms,value,dimension=null){const row=Array(variableCount+1).fill(0);for(const [index,coefficient]of terms)row[index]+=coefficient;row[variableCount]=value;rows.push(row);if(dimension)dimensionRows.push({name:dimension.name,row:rows.length-1,source:dimension.value,editable:dimension.editable});}
  const term=(id,axis,coefficient=1)=>{if(!pointMap.has(id))fail('关系点不属于原草图');return [pointMap.get(id).index*2+axis,coefficient];};
  const radiusTerm=(id,coefficient=1)=>{if(!circleMap.has(id))fail('关系圆不属于原草图');return [pointMap.size*2+circleMap.get(id).index,coefficient];};
  for(const relation of relations){
    if(![1,3,4,5,7,8,9,11,12,14,15,17,25,26,27].includes(relation.type))fail('尚未支持原约束类型 '+relation.type);
    for(const entity of relation.entities){
      if(!entity.id)fail('关系实体没有原始身份');
      if(!pointMap.has(entity.id)&&!lineMap.has(entity.id)&&!circleMap.has(entity.id)){
        if(externalMap.has(entity.id)&&JSON.stringify(externalMap.get(entity.id).entity)!==JSON.stringify(entity))fail('同一外部实体捕获内容不一致');
        externalMap.set(entity.id,{entity});
      }else if(entity.point&&!close(pointMap.get(entity.id)?.coordinates??[],entity.point))fail('关系点坐标与源点不一致');
    }
    if([4,5].includes(relation.type)){if(relation.entities.length!==1||!lineMap.has(relation.entities[0].id))fail('水平/竖直关系没有唯一原线');directions.set(relation.entities[0].id,relation.type===4?0:1);}
  }
  function externalLine(entity,replacement){
    const source=replacement??entity;
    if(source.start&&source.end){const a=project(source.start,sketch.modelToSketch),b=project(source.end,sketch.modelToSketch);const direction=Math.abs(a[0]-b[0])<1e-7?1:Math.abs(a[1]-b[1])<1e-7?0:null;if(direction===null||Math.hypot(a[0]-b[0],a[1]-b[1])<1e-9)fail('外部直线的草图投影不是明确轴向线');return {direction,position:a.map(value=>value*1000)};}
    if(source.referencePlaneTransform){const matrix=source.referencePlaneTransform,origin=project(matrix.slice(9,12),sketch.modelToSketch),normal=projectDirection(matrix.slice(6,9),sketch.modelToSketch),normalAxis=Math.abs(normal[0])>1-1e-7?0:Math.abs(normal[1])>1-1e-7?1:null;if(normalAxis===null)fail('外部基准面未与草图形成轴向直线');return {direction:1-normalAxis,position:origin.map(value=>value*1000)};}
    fail('外部关系没有捕获点、直线或基准面定义');
  }
  // A collinear native reference also fixes a local line's direction.
  for(const relation of relations.filter(row=>row.type===27)){
    const local=relation.entities.filter(entity=>lineMap.has(entity.id)),external=relation.entities.filter(entity=>externalMap.has(entity.id));
    if(local.length===1&&external.length===1)directions.set(local[0].id,externalLine(external[0]).direction);
    else if(local.length!==2||external.length)fail('共线关系没有两个原始直线');
  }
  for(let pass=0;pass<lineMap.size;pass++){
    for(const relation of relations.filter(row=>row.type===27&&row.entities.every(entity=>lineMap.has(entity.id)))){const ids=relation.entities.map(entity=>entity.id);for(const id of ids)if(directions.has(id))for(const other of ids)directions.set(other,directions.get(id));}
    // Symmetric point pairs map both endpoints of a line to the mirrored line.
    const symmetries=relations.filter(row=>row.type===11&&row.entities.length===3&&pointMap.has(row.entities[0].id)&&pointMap.has(row.entities[1].id));
    for(const [id,line]of lineMap)if(directions.has(id))for(const axis of new Set(symmetries.map(row=>row.entities[2].id))){const reflected=line.points.map(point=>symmetries.filter(row=>row.entities[2].id===axis).find(row=>row.entities.slice(0,2).some(entity=>entity.id===point))?.entities.slice(0,2).find(entity=>entity.id!==point)?.id);if(reflected.every(Boolean)){const matches=[...lineMap].filter(([,candidate])=>candidate.points.every(point=>reflected.includes(point)));if(matches.length===1)directions.set(matches[0][0],directions.get(id));}}
  }
  const inferredDirections=[];
  for(const [id,line]of lineMap)if(!directions.has(id)){
    const [first,second]=line.points,axis=[0,1].find(axis=>Math.abs(position(first,1-axis)-position(second,1-axis))<1e-5);
    if(axis!==undefined){directions.set(id,axis);inferredDirections.push(id);}
  }
  const centeredDiagonals=new Map(),diagonalRelations=relations.filter(row=>row.type===9&&row.entities.length===2&&row.entities.some(entity=>pointMap.has(entity.id))&&row.entities.some(entity=>lineMap.has(entity.id)&&!directions.has(entity.id)));
  if(diagonalRelations.length===2){const centerIds=diagonalRelations.map(row=>row.entities.find(entity=>pointMap.has(entity.id)).id),diagonalIds=diagonalRelations.map(row=>row.entities.find(entity=>lineMap.has(entity.id)).id),corners=diagonalIds.flatMap(id=>lineMap.get(id).points),edges=[...lineMap].filter(([id,line])=>directions.has(id)&&line.points.every(point=>corners.includes(point)));
    if(centerIds[0]===centerIds[1]&&new Set(corners).size===4&&edges.length===4&&edges.filter(([id])=>directions.get(id)===0).length===2&&corners.every(point=>edges.filter(([,line])=>line.points.includes(point)).length===2)&&diagonalIds.every(id=>!edges.some(([,line])=>line.points.every(point=>lineMap.get(id).points.includes(point)))))for(const id of diagonalIds)centeredDiagonals.set(id,centerIds[0]);
  }
  const lineDirection=id=>{const direction=directions.get(id);if(direction===undefined)fail('直线方向没有水平/竖直/共线约束');return direction;};
  const center=id=>pointMap.has(id)?id:circleMap.get(id)?.center;
  const signed=(value,measured)=>{if(Math.abs(Math.abs(measured)-value)>1e-4)fail('关系的原始尺寸不能由原几何复现');const sign=Math.sign(measured);if(!sign)fail('零长度关系不能唯一确定尺寸分支');return sign;};
  const referenceRows=[];
  function coordinateEquality(first,second,axis){add([term(first,axis),term(second,axis,-1)],0);}
  function symmetricPoints(first,second,line){const direction=lineDirection(line),normal=1-direction,anchor=lineMap.get(line).points[0];coordinateEquality(first,second,direction);add([term(first,normal),term(second,normal),term(anchor,normal,-2)],0);}
  for(const relation of relations){
    const entities=relation.entities,ids=entities.map(entity=>entity.id),type=relation.type;
    const dimension=relation.dimension?{name:canonical(relation.dimension.name),value:relation.dimension.value*1000,editable:relation.dimension.drivenState===2||governingDimensions.includes(canonical(relation.dimension.name))}:null;
    if([4,5].includes(type)){const line=lineMap.get(ids[0]),axis=1-lineDirection(ids[0]);coordinateEquality(...line.points,axis);}
    else if([7,8].includes(type)){
      // Use the native parallel/perpendicular Jacobian at the captured axes.
      // The nullspace and every solve must independently prove that both lines
      // retain those axes. Thus this is exact on the accepted branch; an
      // unconstrained common rotation cannot be replaced by an assumed angle.
      if(ids.length!==2||ids.some(id=>!lineMap.has(id)))fail('平行/垂直关系需要两条原草图直线');
      const axes=ids.map(lineDirection);if((axes[0]===axes[1])!==(type===7))fail('原平行/垂直关系的方向与几何不一致');
      const terms=[];
      for(const [index,id]of ids.entries()){
        const [first,second]=lineMap.get(id).points,axis=axes[index],length=position(second,axis)-position(first,axis),coefficient=(type===7&&index===1?-1:1)/length;
        if(Math.abs(length)<1e-6)fail('平行/垂直关系包含退化直线');
        terms.push(term(second,1-axis,coefficient),term(first,1-axis,-coefficient));
      }
      add(terms,0);
    }
    else if(type===27){const local=ids.filter(id=>lineMap.has(id)),external=entities.find(entity=>externalMap.has(entity.id));if(local.length===2){const axis=1-lineDirection(local[0]);for(const point of lineMap.get(local[1]).points)coordinateEquality(point,lineMap.get(local[0]).points[0],axis);}else{const line=externalLine(external),axis=1-line.direction;for(const point of lineMap.get(local[0]).points){add([term(point,axis)],line.position[axis]);referenceRows.push({row:rows.length-1,entityId:external.id,axis});}}}
    else if([25,26].includes(type)){
      const axis=type===25?1:0,local=ids.filter(id=>center(id)),reference=entities.find(entity=>!center(entity.id));if(ids.length!==2)fail('点水平/竖直关系缺少两点');
      if(local.length===2)coordinateEquality(center(ids[0]),center(ids[1]),axis);
      else if(local.length===1&&reference?.point){if(Math.abs(position(center(local[0]),axis)-reference.point[axis]*1000)>1e-5)fail('原外部点水平/竖直关系与源坐标不符');add([term(center(local[0]),axis)],reference.point[axis]*1000);referenceRows.push({row:rows.length-1,entityId:reference.id,axis,point:true});}
      else fail('点水平/竖直关系缺少原点');
    }
    else if(type===12){const points=ids.filter(id=>pointMap.has(id)),lines=ids.filter(id=>lineMap.has(id));if(points.length!==1||ids.length!==2)fail('中点关系实体不完整');if(lines.length===1)for(let axis=0;axis<2;axis++)add([term(points[0],axis),...lineMap.get(lines[0]).points.map(id=>term(id,axis,-.5))],0);else{const external=entities.find(entity=>externalMap.has(entity.id));if(!external?.start||!external.end)fail('外部中点关系没有原始边两端点');const a=project(external.start,sketch.modelToSketch),b=project(external.end,sketch.modelToSketch);for(let axis=0;axis<2;axis++){add([term(points[0],axis)],(a[axis]+b[axis])*500);referenceRows.push({row:rows.length-1,entityId:external.id,axis,midpoint:true});}}}
    else if(type===9){
      const points=ids.filter(id=>pointMap.has(id)),lines=ids.filter(id=>lineMap.has(id));
      if(points.length===2&&ids.length===2)for(let axis=0;axis<2;axis++)coordinateEquality(points[0],points[1],axis);
      else if(points.length===1&&lines.length===1&&ids.length===2){if(centeredDiagonals.get(lines[0])===points[0]){for(let axis=0;axis<2;axis++)add([term(points[0],axis),...lineMap.get(lines[0]).points.map(id=>term(id,axis,-.5))],0);}else{const axis=1-lineDirection(lines[0]);coordinateEquality(points[0],lineMap.get(lines[0]).points[0],axis);}}
      else if(points.length===1&&ids.length===2){const external=entities.find(entity=>externalMap.has(entity.id));if(external?.point){for(let axis=0;axis<2;axis++){add([term(points[0],axis)],external.point[axis]*1000);referenceRows.push({row:rows.length-1,entityId:external.id,axis,point:true});}}else{const line=externalLine(external),axis=1-line.direction;add([term(points[0],axis)],line.position[axis]);referenceRows.push({row:rows.length-1,entityId:external.id,axis});}}
      else if(lines.length===1&&ids.length===2&&externalMap.get(ids.find(id=>externalMap.has(id)))?.entity.point){const external=entities.find(entity=>externalMap.has(entity.id)),axis=1-lineDirection(lines[0]);add([term(lineMap.get(lines[0]).points[0],axis)],external.point[axis]*1000);referenceRows.push({row:rows.length-1,entityId:external.id,axis,point:true});}
      else fail('重合关系实体尚未完整识别');
    }else if(type===11){
      if(ids.length!==3||!lineMap.has(ids[2]))fail('对称轴不是原草图线');
      if(center(ids[0])&&center(ids[1])){symmetricPoints(center(ids[0]),center(ids[1]),ids[2]);if(circleMap.has(ids[0])&&circleMap.has(ids[1]))add([radiusTerm(ids[0]),radiusTerm(ids[1],-1)],0);}
      else fail('对称关系尚未支持该原始曲线组合');
    }else if(type===14){
      if(ids.length!==2)fail('等长关系实体数量无效');
      if(ids.every(id=>circleMap.has(id)))add([radiusTerm(ids[0]),radiusTerm(ids[1],-1)],0);
      else if(ids.every(id=>lineMap.has(id))){const terms=[];for(const [index,id]of ids.entries()){const axis=lineDirection(id),[a,b]=lineMap.get(id).points,sign=Math.sign(position(b,axis)-position(a,axis))*(index===0?1:-1);terms.push(term(b,axis,sign),term(a,axis,-sign));}add(terms,0);}
      else fail('等长关系没有对应原始直线或圆');
    }else if([3,15].includes(type)){if(!dimension||ids.length!==1||!circleMap.has(ids[0]))fail('圆径尺寸没有唯一原圆');add([radiusTerm(ids[0],type===15?2:1)],dimension.value,dimension);}
    else if(type===17){if(ids.length!==1||!pointMap.has(ids[0]))fail('固定约束没有唯一原点');for(let axis=0;axis<2;axis++)add([term(ids[0],axis)],position(ids[0],axis));}
    else if(type===1){
      if(!dimension||ids.length!==2)fail('距离关系没有原始尺寸或两实体');
      if(!dimension.editable)continue; // A native driven measurement is an output, not a governing equation.
      // Parallel line distances and point/line distances are normal distances.
      // Captured external points are already expressed in sketch coordinates.
      const anchors=entities.map(entity=>{const localCenter=center(entity.id);if(localCenter)return {point:localCenter};if(entity.point)return {external:entity,pointValue:entity.point.map(value=>value*1000)};if(lineMap.has(entity.id))return {point:lineMap.get(entity.id).points[0],direction:lineDirection(entity.id)};const line=externalLine(entity);return {external:entity,lineValue:line.position,direction:line.direction};});
      const axes=anchors.filter(anchor=>anchor.direction!==undefined).map(anchor=>1-anchor.direction);
      if(new Set(axes).size>1)fail('距离的两条原线不平行');
      const coordinate=(anchor,axis)=>anchor.point?position(anchor.point,axis):(anchor.pointValue??anchor.lineValue)[axis];
      const possible=axes.length?[axes[0]]:[0,1].filter(axis=>Math.abs(Math.abs(coordinate(anchors[1],axis)-coordinate(anchors[0],axis))-dimension.value)<1e-4);
      if(possible.length!==1)fail('距离方向未由原几何唯一确定');
      const axis=possible[0],sign=signed(dimension.value,coordinate(anchors[1],axis)-coordinate(anchors[0],axis)),terms=[];let right=dimension.value;
      for(const [index,anchor]of anchors.entries()){const coefficient=sign*(index===0?-1:1);if(anchor.point)terms.push(term(anchor.point,axis,coefficient));else{right-=coefficient*coordinate(anchor,axis);referenceRows.push({row:rows.length,entityId:anchor.external.id,axis,point:!!anchor.pointValue,coefficient:-coefficient});}}
      add(terms,right,dimension);
    }
  }
  function solve(overrides={},references={}){
    const mutable=rows.map(row=>[...row]);
    for(const [name,value]of Object.entries(overrides)){const matches=dimensionRows.filter(row=>row.name===name&&row.editable);if(matches.length!==1||!Number.isFinite(value)||value<=0)fail('驱动尺寸未对应唯一可控原约束：'+name);const row=matches[0];mutable[row.row][variableCount]+=value-row.source;}
    for(const [id,replacement]of Object.entries(references)){
      const reference=externalMap.get(id);if(!reference)fail('外部更新未对应原始实体');const affected=referenceRows.filter(row=>row.entityId===id);if(!affected.length)fail('外部更新没有原始定位约束');
      const midpoint=entity=>{if(!entity.start||!entity.end)fail('外部中点更新缺少边两端点');const a=project(entity.start,sketch.modelToSketch),b=project(entity.end,sketch.modelToSketch);return a.map((value,index)=>(value+b[index])*500);};
      for(const row of affected){const old=row.point?reference.entity.point.map(value=>value*1000):row.midpoint?midpoint(reference.entity):externalLine(reference.entity).position,updated=row.point?replacement.point?.map(value=>value*1000):row.midpoint?midpoint(replacement):externalLine(reference.entity,replacement).position;if(!updated?.every(Number.isFinite))fail('外部更新定义不完整');mutable[row.row][variableCount]+=(row.coefficient??1)*(updated[row.axis]-old[row.axis]);}
    }
    const solved=solveSystem(mutable),solution=solved.values,updated=structuredClone(sketch),coordinates=id=>[solution[pointMap.get(id).index*2]/1000,solution[pointMap.get(id).index*2+1]/1000,0];
    for(const point of updated.points)point.coordinates=coordinates(point.id);
    for(const segment of updated.segments){
      if(segment.type===0){const line=lineMap.get(segment.id);segment.geometry.start=coordinates(line.points[0]);segment.geometry.end=coordinates(line.points[1]);if(distance(segment.geometry.start,segment.geometry.end)<1e-9)fail('更新后原线退化');}
      else{const circle=circleMap.get(segment.id),radius=solution[pointMap.size*2+circle.index]/1000;if(!(radius>0))fail('更新后孔半径无效');const center=coordinates(circle.center),phase=segment.geometry.start.map((value,index)=>(value-segment.geometry.center[index])/circle.radius);segment.geometry.center=center;segment.geometry.radius=radius;segment.geometry.start=center.map((value,index)=>value+phase[index]*radius);segment.geometry.end=[...segment.geometry.start];}
    }
    for(const relation of updated.relations){
      if(relation.dimension&&Object.hasOwn(overrides,canonical(relation.dimension.name)))relation.dimension.value=overrides[canonical(relation.dimension.name)]/1000;
      for(const entity of relation.entities){if(pointMap.has(entity.id)&&entity.point)entity.point=coordinates(entity.id);else if(Object.hasOwn(references,entity.id))Object.assign(entity,structuredClone(references[entity.id]));}
    }
    verifyInvariants(solution);
    return {sketch:updated,values:Object.fromEntries(dimensionRows.map(row=>[row.name,overrides[row.name]??row.source])),equationCount:mutable.length,variableCount,referenceCount:externalMap.size,freeConstructionCoordinates:solved.freeConstructionCoordinates,...retainFreeCoordinates?{capturedFreeCoordinatePolicy:'retain-original-values-for-ungoverned-preview-coordinates'}:{}};
  }
  const sourceValues=[...[...pointMap.values()].flatMap(point=>point.coordinates.slice(0,2).map(value=>value*1000)),...[...circleMap.values()].map(circle=>circle.radius*1000)],protectedForms=[];
  const protectPoint=id=>{for(let axis=0;axis<2;axis++)protectedForms.push({terms:[term(id,axis)],message:'原草图实体点仍有未确定的自由度'});};
  for(const id of protectedPointIds){if(!pointMap.has(id))fail('要求定位的接口点不属于原草图');protectPoint(id);}
  for(const line of lineMap.values())if(!line.segment.construction)for(const id of line.points)protectPoint(id);
  for(const [id,circle]of circleMap)if(!circle.segment.construction){protectPoint(circle.center);protectedForms.push({terms:[radiusTerm(id)],message:'原草图孔径仍有未确定的自由度'});}
  const invariantForms=[...inferredDirections.map(id=>({terms:lineMap.get(id).points.map((point,index)=>term(point,1-directions.get(id),index===0?1:-1)),message:'直线方向未由原约束唯一确定'})),...endpointAliases.flatMap(aliases=>aliases.slice(1).flatMap(id=>[0,1].map(axis=>({terms:[term(id,axis),term(aliases[0],axis,-1)],message:'同位置端点在原约束驱动下不能证明等价'}))))];
  protectedForms.push(...invariantForms);
  if(preserveCapturedDirections)for(const id of inferredDirections){const line=lineMap.get(id),axis=1-directions.get(id);add([term(line.points[0],axis),term(line.points[1],axis,-1)],0);}
  const solveSystem=(equations,homogeneous=false)=>solveAffineSketchSystem(equations,variableCount,homogeneous?sourceValues.map(()=>0):sourceValues,retainFreeCoordinates?invariantForms:protectedForms);
  function verifyInvariants(solution){for(const form of invariantForms)if(Math.abs(form.terms.reduce((sum,[index,coefficient])=>sum+coefficient*solution[index],0))>1e-5)fail(form.message);}
  const baseline=solve();for(const point of sketch.points)if(!close(point.coordinates,baseline.sketch.points.find(candidate=>candidate.id===point.id).coordinates))fail('约束系统不能复现原始点');
  for(const [id,circle]of circleMap)if(Math.abs(circle.radius-baseline.sketch.segments.find(segment=>segment.id===id).geometry.radius)>1e-7)fail('约束系统不能复现原始孔径');
  // Coincident coordinates alone do not establish endpoint ownership. Accept
  // ambiguous captured endpoints only when the complete native linear system
  // proves those points identical for every independent supported RHS input.
  if(invariantForms.length){for(const input of dimensionRows.filter(row=>row.editable)){const basis=rows.map(row=>[...row.slice(0,variableCount),0]);basis[input.row][variableCount]=1;verifyInvariants(solveSystem(basis,true).values);}}
  return {profileName,dimensions:dimensionRows.map(row=>({name:row.name,value:row.source,editable:row.editable})),references:[...externalMap].map(([id,row])=>({id,...row.entity})),solve};
}
