import {DefinitionError} from './expressions.mjs';
import {solveAffineSketchSystem} from './affine-sketch-system.mjs';
const canonical=name=>name.split('@').slice(0,-1).join('@');
const near=(a,b)=>Math.hypot(...a.map((value,index)=>value-b[index]))<1e-8;
const fail=message=>{throw new DefinitionError('原腰孔组：'+message);};

// A captured face may lack analytic data. For schematic preview only, its
// line-coincidence is redundant when an original edge midpoint and an explicit
// horizontal/vertical relation already determine that construction line.
export function redundantUnresolvedSlotFace(sketch,relation){
  if(relation.suppressed||relation.type!==9||relation.entities.length!==2)return false;
  const face=relation.entities.find(entity=>entity?.definitionReference&&entity.planeParameters===null),line=sketch.segments.find(segment=>segment.id===relation.entities.find(entity=>entity?.id!==face?.id)?.id);
  if(!face||!line?.construction||line.type!==0)return false;
  const axis=[0,1].find(axis=>Math.abs(line.geometry.start[1-axis]-line.geometry.end[1-axis])<1e-8);if(axis===undefined||!sketch.relations.some(row=>!row.suppressed&&row.type===(axis===0?4:5)&&row.entities[0]?.id===line.id))return false;
  const pointIds=sketch.points.filter(point=>near(point.coordinates,line.geometry.start)||near(point.coordinates,line.geometry.end)).map(point=>point.id),midpoints=sketch.relations.filter(row=>!row.suppressed&&row.type===12&&row.entities.some(entity=>pointIds.includes(entity?.id))&&row.entities.some(entity=>entity?.start&&entity.end));
  if(midpoints.length!==1)return false;
  const edge=midpoints[0].entities.find(entity=>entity?.start&&entity.end),middle=edge.start.map((value,index)=>(value+edge.end[index])/2),matrix=sketch.modelToSketch,normal=1-axis,projected=middle[0]*matrix[normal]+middle[1]*matrix[3+normal]+middle[2]*matrix[6+normal]+matrix[9+normal];
  return Math.abs(projected-line.geometry.start[normal])<1e-8;
}

// Some captured ISketchSlot relations contain null entities. Translating each
// original capsule rigidly preserves every possible equal-slot relation. For
// a null symmetry relation, require all original reflected capsule pairs;
// no nearest-object or array-order identity is selected.
export function driveRigidSlotSketch(sketch,overrides={},references={}, {regularPolygons=false}={}){
  const slots=[];
  for(const contour of sketch.contours.filter(row=>row.closed)){
    const segments=sketch.segments.filter(row=>!row.construction&&contour.segments.includes(row.id)),arcs=segments.filter(row=>row.type===1),lines=segments.filter(row=>row.type===0);
    if(!regularPolygons&&segments.length===1&&arcs.length===1&&near(arcs[0].geometry.start,arcs[0].geometry.end)){
      const circle=arcs[0];slots.push({center:circle.geometry.center,radius:circle.geometry.radius,span:0,axis:-1,segments,landmarks:[circle.geometry.center,circle.geometry.start,circle.geometry.end]});continue;
    }
    if(regularPolygons){
      if(arcs.length||segments.length<3||segments.length>12)fail('不是原正多边形孔');
      const vertices=segments.map(row=>row.geometry.start),center=[0,1,2].map(axis=>vertices.reduce((sum,point)=>sum+point[axis],0)/vertices.length),radius=Math.hypot(...vertices[0].map((value,axis)=>value-center[axis])),side=2*radius*Math.sin(Math.PI/vertices.length);
      if(vertices.some(point=>Math.abs(Math.hypot(...point.map((value,axis)=>value-center[axis]))-radius)>1e-8)||segments.some(row=>Math.abs(Math.hypot(...row.geometry.end.map((value,axis)=>value-row.geometry.start[axis]))-side)>1e-8))fail('原孔不是等边共圆的正多边形');
      const construction=sketch.segments.filter(row=>row.construction&&row.type===1&&near(row.geometry.center,center));
      if(!construction.length)fail('原正多边形没有同心构造圆');
      slots.push({center,radius,span:side,axis:-1,segments:[...segments,...construction],landmarks:[center,...segments.flatMap(row=>[row.geometry.start,row.geometry.end]),...construction.flatMap(row=>[row.geometry.start,row.geometry.end,row.geometry.center])]});continue;
    }
    if(segments.length!==4||arcs.length!==2||lines.length!==2||Math.abs(arcs[0].geometry.radius-arcs[1].geometry.radius)>1e-8)fail('轮廓不是完整原双圆弧腰孔');
    const centers=arcs.map(row=>row.geometry.center),delta=centers[1].map((value,index)=>value-centers[0][index]),axis=[0,1].find(index=>Math.abs(delta[1-index])<1e-8&&Math.abs(delta[index])>1e-8);
    if(axis===undefined||lines.some(row=>Math.abs(row.geometry.start[1-axis]-row.geometry.end[1-axis])>1e-8))fail('原腰孔轴线不是明确轴向');
    const radius=arcs[0].geometry.radius,span=Math.abs(delta[axis]),center=centers[0].map((value,index)=>(value+centers[1][index])/2),landmarks=[center,...segments.flatMap(row=>[row.geometry.start,row.geometry.end,...row.type===1?[row.geometry.center]:[]]),...centers.map(point=>point.map((value,index)=>value+(index===axis?Math.sign(value-center[axis])*radius:0)))];
    if(arcs.some(row=>!near(row.geometry.start.map((value,index)=>value-row.geometry.center[index]),row.geometry.end.map((value,index)=>row.geometry.center[index]-value))))fail('原腰孔端部不是半圆');
    slots.push({center,radius,span,axis,segments,landmarks});
  }
  if(!slots.length||slots.length>40)fail('没有完整可控腰孔');
  const points=new Map(sketch.points.map((row,index)=>[row.id,{...row,index}])),lines=new Map(),segments=new Map(sketch.segments.map(row=>[row.id,row])),rows=[],dimensionRows=[],external=[];
  const variableCount=points.size*2+slots.length*2,source=[...sketch.points.flatMap(row=>row.coordinates.slice(0,2).map(value=>value*1000)),...slots.flatMap(row=>row.center.slice(0,2).map(value=>value*1000))];
  const add=(terms,value)=>{const row=Array(variableCount+1).fill(0);for(const [column,coefficient]of terms)row[column]+=coefficient;row[variableCount]=value;rows.push(row);};
  const term=(id,axis,coefficient=1)=>{if(!points.has(id))fail('缺少原始草图点');return [points.get(id).index*2+axis,coefficient];};
  const centerTerm=(index,axis,coefficient=1)=>[points.size*2+index*2+axis,coefficient];
  const pointAt=coordinates=>{const matches=[...points.values()].filter(row=>near(row.coordinates,coordinates));if(!matches.length)fail('原直线没有捕获端点');return matches[0].id;};
  const ownerOfPoint=point=>slots.flatMap((slot,index)=>slot.landmarks.some(mark=>near(mark,point))||regularPolygons&&Math.hypot(...point.map((value,axis)=>value-slot.center[axis]))<=slot.radius+1e-9?[index]:[]);
  const ownerOfEntity=entity=>{if(!entity)return -1;const segment=segments.get(entity.id);if(segment)return slots.findIndex(slot=>slot.segments.includes(segment));const point=points.get(entity.id);return point?ownerOfPoint(point.coordinates)[0]??-1:-1;};
  for(const segment of sketch.segments.filter(row=>row.type===0)){
    const a=pointAt(segment.geometry.start),b=pointAt(segment.geometry.end),axis=[0,1].find(index=>Math.abs(segment.geometry.start[1-index]-segment.geometry.end[1-index])<1e-8);
    if(axis===undefined&&!regularPolygons)fail('包含非轴向构造线');lines.set(segment.id,{a,b,axis});
  }
  for(const point of points.values()){
    const owners=ownerOfPoint(point.coordinates);
    if(owners.length>1)fail('原腰孔点同时属于多个实体轮廓');
    if(owners.length){const index=owners[0];for(let axis=0;axis<2;axis++)add([term(point.id,axis),centerTerm(index,axis,-1)],(point.coordinates[axis]-slots[index].center[axis])*1000);}
  }
  const project=point=>[0,1,2].map(axis=>point[0]*sketch.modelToSketch[axis]+point[1]*sketch.modelToSketch[3+axis]+point[2]*sketch.modelToSketch[6+axis]+sketch.modelToSketch[9+axis]);
  const coincidentReferences=new Map();for(const relation of sketch.relations.filter(row=>!row.suppressed&&row.type===9&&row.entities.length===2)){
    const local=relation.entities.find(entity=>points.has(entity?.id)),external=relation.entities.find(entity=>entity&&!points.has(entity.id)&&(entity.referenceOnly||references[entity.id]?.point));if(local&&external)coincidentReferences.set(external.id,points.get(local.id).coordinates);
  }
  const anchors=entity=>{
    if(points.has(entity?.id))return {point:entity.id,coordinates:points.get(entity.id).coordinates};
    if(lines.has(entity?.id)){const line=lines.get(entity.id);return {point:line.a,coordinates:points.get(line.a).coordinates,axis:line.axis};}
    if(segments.get(entity?.id)?.type===1){const arc=segments.get(entity.id);return {point:pointAt(arc.geometry.center),coordinates:arc.geometry.center};}
    if(entity?.point||coincidentReferences.has(entity?.id)){external.push(entity);return {coordinates:entity.point??coincidentReferences.get(entity.id),updatedCoordinates:references[entity.id]?.point};}
    if(entity?.circleParameters){const replacement=references[entity.id];return {coordinates:project(entity.circleParameters.slice(0,3)),updatedCoordinates:replacement?.circleParameters?project(replacement.circleParameters.slice(0,3)):null};}
    if(entity?.start&&entity.end){const first=project(entity.start),second=project(entity.end),axis=[0,1].find(index=>Math.abs(first[1-index]-second[1-index])<1e-8);if(axis===undefined)fail('外部直线不是原轴向');const replacement=references[entity.id];return {coordinates:first,updatedCoordinates:replacement?.start?project(replacement.start):null,axis};}
    fail('关系实体不是原点或轴向线');
  };
  function difference(first,second,axis,value){const terms=[];let right=value;for(const [anchor,coefficient]of [[first,-1],[second,1]]){if(anchor.point)terms.push(term(anchor.point,axis,coefficient));else right-=coefficient*(anchor.updatedCoordinates??anchor.coordinates)[axis]*1000;}add(terms,right);}
  let recoveredSymmetryPairs=0,redundantUnresolvedFaces=0;
  for(const relation of sketch.relations.filter(row=>!row.suppressed)){
    const entities=relation.entities,type=relation.type;
    if(redundantUnresolvedSlotFace(sketch,relation)){redundantUnresolvedFaces++;continue;}
    if(regularPolygons&&[6,57].includes(type)){
      const owners=entities.map(ownerOfEntity);if(owners.length!==2||owners[0]<0||owners[0]!==owners[1])fail('正多边形形状关系跨越不同原孔');
      if(type===6){const line=entities.map(entity=>segments.get(entity.id)).find(row=>row?.type===0),circle=entities.map(entity=>segments.get(entity.id)).find(row=>row?.type===1);if(!line||!circle)fail('相切关系没有原圆与边');const a=line.geometry.start,b=line.geometry.end,c=circle.geometry.center,length=Math.hypot(b[0]-a[0],b[1]-a[1]),distance=Math.abs((b[0]-a[0])*(a[1]-c[1])-(a[0]-c[0])*(b[1]-a[1]))/length;if(Math.abs(distance-circle.geometry.radius)>1e-8)fail('正多边形原相切关系不符');}
      continue;
    }
    if(type===75){if(slots.some(slot=>Math.abs(slot.radius-slots[0].radius)>1e-8||Math.abs(slot.span-slots[0].span)>1e-8))fail('空实体等腰孔约束无法由保持原形证明');continue;}
    if(type===14){
      if(entities.length!==2)fail('等长关系缺少两条原曲线');
      const curves=entities.map(entity=>segments.get(entity?.id)??(entity?.circleParameters?{type:1,geometry:{radius:(references[entity.id]??entity).circleParameters[6]},external:true}:entity?.start&&entity.end?{type:0,geometry:{start:(references[entity.id]??entity).start,end:(references[entity.id]??entity).end},external:true}:null));
      if(curves.some(curve=>!curve))fail('等长曲线不属于原腰孔');
      const lengths=curves.map(curve=>curve.type===1?curve.geometry.radius:Math.hypot(...curve.geometry.end.map((value,axis)=>value-curve.geometry.start[axis])));
      if(curves[0].type!==curves[1].type||Math.abs(lengths[0]-lengths[1])>1e-8||curves.some(curve=>!curve.external&&!slots.some(slot=>slot.segments.includes(curve))))fail('等长关系不能由保持原腰孔形状证明');
      for(const entity of entities.filter(entity=>entity?.circleParameters)){const center=entity.circleParameters.slice(0,3);if(!entity.start||!entity.end||!near(entity.start.map((value,axis)=>value-center[axis]),entity.end.map((value,axis)=>center[axis]-value))||Math.hypot(...entity.start.map((value,axis)=>value-center[axis]))<1e-9)fail('外部等长圆弧不是与原腰孔相同的半圆');}
      continue;
    }
    if([3,15].includes(type)){
      const arc=segments.get(entities[0]?.id),dimension=relation.dimension;
      const name=dimension&&canonical(dimension.name);if(entities.length!==1||arc?.type!==1||!dimension||Math.abs(dimension.value-arc.geometry.radius*(type===15?2:1))>1e-8||Object.hasOwn(overrides,name)&&Math.abs(overrides[name]-dimension.value*1000)>1e-6)fail('圆弧半径未保持原腰孔形状');if(Object.hasOwn(overrides,name))dimensionRows.push(name);continue;
    }
    if(type===11&&entities[0]===null&&entities[1]===null){
      const line=lines.get(entities[2]?.id);if(!line)fail('空实体对称没有原始轴线');const axis=1-line.axis,station=points.get(line.a).coordinates[axis],pairs=[];
      for(let first=0;first<slots.length;first++)for(let second=first+1;second<slots.length;second++){const a=slots[first],b=slots[second];if(a.axis===b.axis&&Math.abs(a.radius-b.radius)<1e-8&&Math.abs(a.span-b.span)<1e-8&&Math.abs(a.center[line.axis]-b.center[line.axis])<1e-8&&Math.abs(a.center[axis]+b.center[axis]-2*station)<1e-8&&Math.abs(a.center[axis]-b.center[axis])>1e-8)pairs.push([first,second]);}
      const relationCount=sketch.relations.filter(row=>!row.suppressed&&row.type===11&&row.entities[0]===null&&row.entities[1]===null&&row.entities[2]?.id===entities[2].id).length;
      if(pairs.length!==relationCount||new Set(pairs.flat()).size!==pairs.length*2)fail('空实体对称不能由原轮廓唯一分组');
      for(const [first,second]of pairs){add([centerTerm(first,axis),centerTerm(second,axis),term(line.a,axis,-2)],0);add([centerTerm(first,line.axis),centerTerm(second,line.axis,-1)],0);}
      recoveredSymmetryPairs=pairs.length;continue;
    }
    if([4,5].includes(type)){const line=lines.get(entities[0]?.id);if(!line||entities.length!==1||line.axis!==(type===4?0:1))fail('原水平或竖直关系不符');difference(anchors({id:line.a}),anchors({id:line.b}),1-line.axis,0);continue;}
    if(type===12&&entities.some(entity=>segments.get(entity?.id)?.type===1)){
      const arc=segments.get(entities.find(entity=>segments.get(entity?.id)?.type===1).id),point=entities.find(entity=>points.has(entity?.id));if(!point||!slots.some(slot=>slot.segments.includes(arc)&&slot.landmarks.some(mark=>near(mark,points.get(point.id).coordinates))))fail('圆弧中点未属于原腰孔');continue;
    }
    if(type===12){
      const point=entities.find(entity=>points.has(entity?.id)),local=entities.find(entity=>lines.has(entity?.id)),edge=entities.find(entity=>entity?.start&&entity.end);if(!point||entities.length!==2)fail('中点关系未对应原点和边');
      if(local){const line=lines.get(local.id);for(let axis=0;axis<2;axis++)add([term(point.id,axis),term(line.a,axis,-.5),term(line.b,axis,-.5)],0);}
      else if(edge){const changed=references[edge.id]??edge,first=project(changed.start),second=project(changed.end);for(let axis=0;axis<2;axis++)add([term(point.id,axis)],(first[axis]+second[axis])*500);}
      else fail('中点关系缺少原边');continue;
    }
    if(type===11){
      if(regularPolygons&&entities.slice(0,2).every(entity=>points.has(entity?.id))){const line=lines.get(entities[2]?.id);if(!line||line.axis===undefined)fail('对称轴不是原轴向线');const axis=1-line.axis;add([term(entities[0].id,axis),term(entities[1].id,axis),term(line.a,axis,-2)],0);add([term(entities[0].id,line.axis),term(entities[1].id,line.axis,-1)],0);continue;}
      const line=lines.get(entities[2]?.id),owners=entities.slice(0,2).map(entity=>slots.findIndex(slot=>slot.segments.some(segment=>segment.id===entity?.id)));
      if(!line||owners.some(index=>index<0)||owners[0]===owners[1])fail('对称关系没有两组原腰孔和原轴');
      const axis=1-line.axis,first=slots[owners[0]],second=slots[owners[1]],station=points.get(line.a).coordinates[axis];
      if(Math.abs(first.center[axis]+second.center[axis]-2*station)>1e-8||Math.abs(first.center[line.axis]-second.center[line.axis])>1e-8||first.axis!==second.axis||Math.abs(first.radius-second.radius)>1e-8||Math.abs(first.span-second.span)>1e-8)fail('原腰孔没有满足捕获的对称关系');
      add([centerTerm(owners[0],axis),centerTerm(owners[1],axis),term(line.a,axis,-2)],0);add([centerTerm(owners[0],line.axis),centerTerm(owners[1],line.axis,-1)],0);continue;
    }
    if(type===27&&regularPolygons){const local=entities.find(entity=>lines.has(entity?.id)),external=entities.find(entity=>!lines.has(entity?.id));if(!local||!external)fail('共线关系缺少原外部边');const line=lines.get(local.id),anchor=anchors(external);if(line.axis===undefined||anchor.axis!==line.axis)fail('原共线方向不符');for(const point of [line.a,line.b])difference(anchors({id:point}),anchor,1-line.axis,0);continue;}
    if([9,25,26,1].includes(type)){
      if(entities.length!==2||entities.some(entity=>!entity))fail('定位关系缺少两实体');const first=anchors(entities[0]),second=anchors(entities[1]);
      if(regularPolygons&&type===1&&ownerOfEntity(entities[0])>=0&&ownerOfEntity(entities[0])===ownerOfEntity(entities[1])){const dimension=relation.dimension,name=dimension&&canonical(dimension.name);if(!dimension||Math.abs(Math.hypot(...first.coordinates.map((value,axis)=>value-second.coordinates[axis]))-dimension.value)>1e-8||Object.hasOwn(overrides,name)&&Math.abs(overrides[name]-dimension.value*1000)>1e-6)fail('原正多边形孔内尺寸未保持');if(Object.hasOwn(overrides,name))dimensionRows.push(name);continue;}
      if([25,26].includes(type)){difference(first,second,type===25?1:0,0);continue;}
      if(type===9){const axis=first.axis??second.axis;if(axis!==undefined)difference(first,second,1-axis,0);else for(let index=0;index<2;index++)difference(first,second,index,0);continue;}
      if(!relation.dimension)fail('距离关系没有原始尺寸');const name=canonical(relation.dimension.name),sourceValue=relation.dimension.value*1000;
      if(relation.dimension.drivenState!==2&&!Object.hasOwn(overrides,name))continue;
      const lineAxis=first.axis??second.axis,possible=lineAxis===undefined?[0,1].filter(axis=>Math.abs(Math.abs((second.coordinates[axis]-first.coordinates[axis])*1000)-sourceValue)<1e-5):[1-lineAxis];
      if(possible.length!==1)fail('距离方向不能由原关系复现');const axis=possible[0],measured=(second.coordinates[axis]-first.coordinates[axis])*1000,sign=Math.sign(measured);
      if(!sign||Math.abs(Math.abs(measured)-sourceValue)>1e-5)fail('距离不能由原坐标复现');const value=overrides[name]??sourceValue;if(!Number.isFinite(value)||value<0)fail('驱动距离无效');difference(first,second,axis,sign*value);dimensionRows.push(name);continue;
    }
    fail('尚未支持该腰孔原关系 '+type);
  }
  if(Object.keys(overrides).some(name=>!dimensionRows.includes(name)))fail('输入尺寸未作用原始定位约束');
  const protectedForms=slots.flatMap((slot,index)=>[0,1].map(axis=>({terms:[centerTerm(index,axis)],message:'原腰孔组定位仍有未确定的自由度'}))),solution=solveAffineSketchSystem(rows,variableCount,source,protectedForms),updated=structuredClone(sketch),movements=slots.map((slot,index)=>[0,1,2].map(axis=>axis===2?0:solution.values[points.size*2+index*2+axis]/1000-slot.center[axis]));
  for(const segment of updated.segments){
    const index=slots.findIndex(slot=>slot.segments.some(row=>row.id===segment.id));
    if(index>=0){for(const name of ['start','end','center'])if(segment.geometry[name])segment.geometry[name]=segment.geometry[name].map((value,axis)=>value+movements[index][axis]);}
    else if(segment.type===0){const line=lines.get(segment.id);segment.geometry.start=[solution.values[points.get(line.a).index*2]/1000,solution.values[points.get(line.a).index*2+1]/1000,0];segment.geometry.end=[solution.values[points.get(line.b).index*2]/1000,solution.values[points.get(line.b).index*2+1]/1000,0];}
  }
  for(const point of updated.points)point.coordinates=[solution.values[points.get(point.id).index*2]/1000,solution.values[points.get(point.id).index*2+1]/1000,0];
  for(const relation of updated.relations){if(relation.dimension&&Object.hasOwn(overrides,canonical(relation.dimension.name)))relation.dimension.value=overrides[canonical(relation.dimension.name)]/1000;for(const entity of relation.entities)if(entity?.point&&points.has(entity.id))entity.point=[...updated.points.find(row=>row.id===entity.id).coordinates];}
  return {sketch:updated,evidence:{source:regularPolygons?'native-rigid-regular-polygons-and-complete-position-relations':'native-rigid-capsules-and-complete-position-relations',slotCount:slots.length,recoveredSymmetryPairs,redundantUnresolvedFaces,unresolvedFacePolicy:redundantUnresolvedFaces?'schematic-line-position-from-native-edge-midpoint':null,equationCount:rows.length,freeConstructionCoordinates:solution.freeConstructionCoordinates},slots:slots.map((slot,index)=>({centerMm:slot.center.map((value,axis)=>(value+movements[index][axis])*1000),movementMm:movements[index].map(value=>value*1000)}))};
}
