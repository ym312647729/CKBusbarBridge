import {mixedMeshPatches} from './mesh-patches.mjs';
import {planarHolePatches,simplifiedBoundary} from './planar-hole-patches.mjs';

const subtract=(left,right)=>left.map((value,index)=>value-right[index]);
const dot=(left,right)=>left.reduce((sum,value,index)=>sum+value*right[index],0);
const cross=(left,right)=>[left[1]*right[2]-left[2]*right[1],left[2]*right[0]-left[0]*right[2],left[0]*right[1]-left[1]*right[0]];
const unit=vector=>{const magnitude=Math.hypot(...vector);return vector.map(value=>value/magnitude);};
const signedArea=loop=>loop.reduce((sum,point,index)=>sum+point[0]*loop[(index+1)%loop.length][1]-point[1]*loop[(index+1)%loop.length][0],0)/2;
const edgeKey=(from,to)=>from<to?from+':'+to:to+':'+from;

function triangleTopology(positions,indices){
  const edges=new Map(),vertices=new Set(indices),degenerate=new Set();
  for(let offset=0;offset<indices.length;offset+=3){
    const vertices=indices.slice(offset,offset+3),points=Array.from(vertices,index=>positions.slice(index*3,index*3+3));
    if(new Set(vertices).size!==3||Math.hypot(...cross(subtract(points[1],points[0]),subtract(points[2],points[0])))===0)degenerate.add(Array.from(vertices).sort((left,right)=>left-right).join(':'));
    for(let corner=0;corner<3;corner++){const from=vertices[corner],to=vertices[(corner+1)%3],key=edgeKey(from,to),edge=edges.get(key)??{count:0,balance:0};edge.count++;edge.balance+=from<to?1:-1;edges.set(key,edge);}
  }
  return {closed:[...edges.values()].every(edge=>edge.count===2&&edge.balance===0),euler:vertices.size-edges.size+indices.length/3,degenerate};
}

export function meshFaceLabels(mesh){
  if(mesh.faceID?.length!==mesh.triVerts.length/3)return undefined;
  const labels=new Uint32Array(mesh.faceID.length),identities=new Map();let run=0;
  for(let triangle=0;triangle<labels.length;triangle++){
    while(run+1<(mesh.runIndex?.length??0)&&triangle*3>=mesh.runIndex[run+1])run++;
    const identity=run+':'+mesh.faceID[triangle];if(!identities.has(identity))identities.set(identity,identities.size);labels[triangle]=identities.get(identity);
  }
  return labels;
}

function vertexComponents(indices,count){
  const parents=Array.from({length:count},(_,index)=>index),find=index=>{let root=index;while(parents[root]!==root)root=parents[root];while(parents[index]!==index){const next=parents[index];parents[index]=root;index=next;}return root;};
  for(let offset=0;offset<indices.length;offset+=3){const root=find(indices[offset]);for(let corner=1;corner<3;corner++)parents[find(indices[offset+corner])]=root;}
  return parents.map((_,index)=>find(index));
}

function faceComponents(indices,faceIds){
  const parents=Array.from({length:indices.length/3},(_,index)=>index),find=index=>{let root=index;while(parents[root]!==root)root=parents[root];while(parents[index]!==index){const next=parents[index];parents[index]=root;index=next;}return root;},edges=new Map();
  for(let triangle=0;triangle<parents.length;triangle++)for(let corner=0;corner<3;corner++){
    const from=indices[triangle*3+corner],to=indices[triangle*3+(corner+1)%3],key=edgeKey(from,to),prior=edges.get(key);
    if(prior&&faceIds[prior.triangle]===faceIds[triangle]&&prior.from===to&&prior.to===from)parents[find(triangle)]=find(prior.triangle);
    else edges.set(key,{triangle,from,to});
  }
  const groups=new Map();for(let triangle=0;triangle<parents.length;triangle++){const root=find(triangle);if(!groups.has(root))groups.set(root,[]);groups.get(root).push(triangle);}return groups.values();
}
function faceBoundary(triangles,indices){
  const edges=new Map();for(const triangle of triangles)for(let corner=0;corner<3;corner++){
    const from=indices[triangle*3+corner],to=indices[triangle*3+(corner+1)%3],key=edgeKey(from,to);
    if(edges.has(key))edges.delete(key);else edges.set(key,{from,to});
  }
  const next=new Map();for(const {from,to}of edges.values()){if(next.has(from))return null;next.set(from,to);}
  const loops=[];while(next.size){const start=next.keys().next().value,loop=[],seen=new Set();let current=start;
    while(!seen.has(current)){if(!next.has(current))return null;loop.push(current);seen.add(current);const to=next.get(current);next.delete(current);current=to;}
    if(current!==start||loop.length<3)return null;loops.push(loop);
  }
  return loops;
}
function partitionFace(triangles,positions,indices,epsilon){
  const loops=faceBoundary(triangles,indices);if(!loops||loops.length<2)return null;
  const sourcePoint=index=>Array.from(positions.slice(index*3,index*3+3));
  if(loops.some(loop=>loop.some((index,ordinal)=>Math.hypot(...subtract(sourcePoint(index),sourcePoint(loop[(ordinal+1)%loop.length])))===0))||triangles.some(triangle=>{const [a,b,c]=[0,1,2].map(corner=>sourcePoint(indices[triangle*3+corner]));return Math.hypot(...cross(subtract(b,a),subtract(c,a)))===0;}))return {precisionLimited:true};
  const point=index=>Array.from(positions.slice(index*3,index*3+3)),first=triangles[0]*3,firstPoint=point(indices[first]),firstNormal=cross(subtract(point(indices[first+1]),firstPoint),subtract(point(indices[first+2]),firstPoint));
  const boundaryEdges=loops.flatMap(loop=>loop.map((from,index)=>({origin:point(from),direction:subtract(point(loop[(index+1)%loop.length]),point(from))}))).sort((left,right)=>dot(right.direction,right.direction)-dot(left.direction,left.direction)),origin=boundaryEdges[0].origin,a=boundaryEdges[0].direction;
  const other=boundaryEdges.reduce((chosen,edge)=>dot(cross(a,edge.direction),cross(a,edge.direction))>dot(cross(a,chosen.direction),cross(a,chosen.direction))?edge:chosen,boundaryEdges[0]);
  let normal=unit(cross(a,other.direction));if(dot(normal,firstNormal)<0)normal=normal.map(value=>-value);
  if(!normal.every(Number.isFinite))return null;
  for(const triangle of triangles)for(let corner=0;corner<3;corner++)if(Math.abs(dot(subtract(point(indices[triangle*3+corner]),origin),normal))>epsilon)return null;
  let along=unit(a),across=cross(normal,along);
  const project=value=>{const delta=subtract(value,origin);return [dot(delta,along),dot(delta,across)];};
  const projected=loops.map(loop=>loop.map(index=>project(point(index)))),outerCandidates=projected.flatMap((loop,index)=>signedArea(loop)>0?[index]:[]);
  if(outerCandidates.length!==1)return null;
  const outerIndex=outerCandidates[0],simplified=simplifiedBoundary(projected[outerIndex],epsilon);
  if(simplified.length!==4)return null;
  const firstDirection=subtract(simplified[1],simplified[0]);along=unit(along.map((value,index)=>value*firstDirection[0]+across[index]*firstDirection[1]));across=cross(normal,along);
  const aligned=loops.map(loop=>loop.map(index=>project(point(index)))),holes=aligned.filter((_,index)=>index!==outerIndex),partition=planarHolePatches(aligned[outerIndex],holes,epsilon);
  if(!partition)return null;
  const sourcePoints=new Map();aligned.forEach((loop,loopIndex)=>loop.forEach((value,index)=>sourcePoints.set(value,{point:point(loops[loopIndex][index]),sourceIndex:loops[loopIndex][index]})));
  const outer=aligned[outerIndex],outerIndices=loops[outerIndex];
  const lift=value=>{
    if(sourcePoints.has(value))return sourcePoints.get(value);
    for(let index=0;index<outer.length;index++)if(Math.hypot(...subtract(value,outer[index]))<=epsilon)return {point:point(outerIndices[index]),sourceIndex:outerIndices[index]};
    for(let index=0;index<outer.length;index++){
      const start=outer[index],end=outer[(index+1)%outer.length],delta=subtract(value,start),direction=subtract(end,start),parameter=dot(delta,direction)/dot(direction,direction);
      if(parameter<0||parameter>1||Math.hypot(...delta.map((coordinate,axis)=>coordinate-direction[axis]*parameter))>epsilon)continue;
      const sourceStart=point(outerIndices[index]),sourceEnd=point(outerIndices[(index+1)%outer.length]);return {point:sourceStart.map((coordinate,axis)=>coordinate+(sourceEnd[axis]-coordinate)*parameter),sourceEdge:edgeKey(outerIndices[index],outerIndices[(index+1)%outer.length])};
    }
    return {point:origin.map((coordinate,index)=>coordinate+along[index]*value[0]+across[index]*value[1])};
  };
  return {partition,lift,boundaryIndices:loops.flat(),boundaryCoordinates:aligned.flat()};
}

// Retriangulate only proven rectangular planar faces; the Manifold solid stays unchanged.
export function planarMixedMesh(positions,indices,faceIds){
  const coordinateScale=Math.max(1,...[0,1,2].map(axis=>{let lower=Infinity,upper=-Infinity;for(let index=axis;index<positions.length;index+=3){lower=Math.min(lower,positions[index]);upper=Math.max(upper,positions[index]);}return upper-lower;})),epsilon=Math.max(1e-9,coordinateScale*1e-11);
  const selected=new Set(),partitions=[];let precisionLimitedFaces=0;
  const floatPlaneTolerance=Math.max(1e-7,coordinateScale*3e-7);
  if(faceIds?.length===indices.length/3)for(const triangles of faceComponents(indices,faceIds)){
    if(triangles.length<6)continue;const result=partitionFace(triangles,positions,indices,floatPlaneTolerance);if(!result)continue;if(result.precisionLimited){precisionLimitedFaces++;continue;}
    partitions.push(result);triangles.forEach(index=>selected.add(index));
  }
  const precisionEvidence=precisionLimitedFaces?{precisionLimitedFaces}:{};
  if(!partitions.length)return {positions:Float32Array.from(positions),...mixedMeshPatches(positions,indices,faceIds),planarPartitions:{faces:0,holes:0,localFrames:0,...precisionEvidence}};
  const retainedIndices=[],retainedFaceIds=[];for(let triangle=0;triangle<indices.length/3;triangle++)if(!selected.has(triangle)){retainedIndices.push(...indices.slice(triangle*3,triangle*3+3));retainedFaceIds.push(faceIds[triangle]);}
  const retained=mixedMeshPatches(positions,Uint32Array.from(retainedIndices),retainedFaceIds),coordinates=Array.from(positions),coordinateBuckets=new Map(),components=vertexComponents(indices,positions.length/3),boundaryCandidates=new Set(),pointSources=new Map(),patchFaces=new Map(),sourceEdges=new Set();
  for(let offset=0;offset<indices.length;offset+=3)for(let corner=0;corner<3;corner++)sourceEdges.add(edgeKey(indices[offset+corner],indices[offset+(corner+1)%3]));
  function addPoint(value,componentId,namespace){
    const cell=value.map(coordinate=>Math.round(coordinate/epsilon));
    for(let x=-1;x<=1;x++)for(let y=-1;y<=1;y++)for(let z=-1;z<=1;z++){const bucket=coordinateBuckets.get(namespace+':'+[cell[0]+x,cell[1]+y,cell[2]+z].join(':'));for(const candidate of bucket??[])if(value.every((coordinate,axis)=>Math.abs(coordinate-coordinates[candidate*3+axis])<=epsilon))return candidate;}
    const index=coordinates.length/3,key=namespace+':'+cell.join(':');coordinates.push(...value);components.push(componentId);if(!coordinateBuckets.has(key))coordinateBuckets.set(key,[]);coordinateBuckets.get(key).push(index);return index;
  }
  function registerPoint(index,face,sourceEdge,localPoint){if(!pointSources.has(index))pointSources.set(index,{faces:new Map(),edges:new Set()});const source=pointSources.get(index);source.faces.set(face,localPoint);if(sourceEdge)source.edges.add(sourceEdge);boundaryCandidates.add(index);}
  const quads=[],triangles=[];
  for(let index=0;index<retained.quadIndices.length;index+=4)quads.push(Array.from(retained.quadIndices.slice(index,index+4)));
  for(let index=0;index<retained.triangleIndices.length;index+=3)triangles.push(Array.from(retained.triangleIndices.slice(index,index+3)));
  for(const [face,{partition,lift,boundaryIndices,boundaryCoordinates}]of partitions.entries()){
    const componentId=components[boundaryIndices[0]];boundaryIndices.forEach((index,ordinal)=>registerPoint(index,face,null,boundaryCoordinates[ordinal]));
    for(const [pieces,target]of [[partition.quads,quads],[partition.triangles,triangles]])for(const piece of pieces){const polygon=piece.map(value=>{const lifted=lift(value),index=lifted.sourceIndex??addPoint(lifted.point,componentId,lifted.sourceEdge?'edge:'+lifted.sourceEdge:'face:'+face);registerPoint(index,face,lifted.sourceEdge,value);return index;});patchFaces.set(polygon,face);target.push(polygon);}
  }
  // Split every adjoining compatibility edge at the same vertex. This prevents T junctions
  // both between large rectangles and on the original outer walls / hole walls.
  const candidatePoints=[...boundaryCandidates].map((index,ordinal)=>({index,ordinal,point:coordinates.slice(index*3,index*3+3)})),candidatesByFace=new Map(),candidatesByEdge=new Map(),render=[];
  for(const candidate of candidatePoints){
    const provenance=pointSources.get(candidate.index);
    for(const [identities,lookup]of [[provenance.faces.keys(),candidatesByFace],[provenance.edges,candidatesByEdge]])for(const identity of identities){if(!lookup.has(identity))lookup.set(identity,[]);lookup.get(identity).push(candidate);}
  }
  // Both lists retain the original candidate order. Merge their union so ties in
  // edge parameters produce exactly the same compatibility indices as before.
  function edgeCandidates(face,originalEdge){
    const faceCandidates=candidatesByFace.get(face)??[],sourceCandidates=candidatesByEdge.get(originalEdge)??[];
    if(!faceCandidates.length)return sourceCandidates;if(!sourceCandidates.length)return faceCandidates;
    const merged=[];let faceCursor=0,sourceCursor=0;
    while(faceCursor<faceCandidates.length||sourceCursor<sourceCandidates.length){
      const faceCandidate=faceCandidates[faceCursor],sourceCandidate=sourceCandidates[sourceCursor];
      if(!sourceCandidate||faceCandidate&&faceCandidate.ordinal<sourceCandidate.ordinal){merged.push(faceCandidate);faceCursor++;}
      else if(!faceCandidate||sourceCandidate.ordinal<faceCandidate.ordinal){merged.push(sourceCandidate);sourceCursor++;}
      else{merged.push(faceCandidate);faceCursor++;sourceCursor++;}
    }
    return merged;
  }
  for(const polygon of [...quads,...triangles]){
    const boundary=[],face=patchFaces.get(polygon);let split=false;
    for(let edge=0;edge<polygon.length;edge++){
      const from=polygon[edge],to=polygon[(edge+1)%polygon.length],start=coordinates.slice(from*3,from*3+3),end=coordinates.slice(to*3,to*3+3),direction=subtract(end,start),lengthSquared=dot(direction,direction),interior=[];
      const originalEdge=from<positions.length/3&&to<positions.length/3&&sourceEdges.has(edgeKey(from,to))?edgeKey(from,to):null;
      boundary.push(from);if(lengthSquared<=epsilon*epsilon)continue;
      for(const candidate of edgeCandidates(face,originalEdge)){const provenance=pointSources.get(candidate.index);if(components[candidate.index]!==components[from]||polygon.includes(candidate.index)||candidate.point.some((value,axis)=>value<Math.min(start[axis],end[axis])-floatPlaneTolerance||value>Math.max(start[axis],end[axis])+floatPlaneTolerance))continue;
        const localStart=pointSources.get(from)?.faces.get(face),localEnd=pointSources.get(to)?.faces.get(face),localCandidate=provenance.faces.get(face),edgeStart=localStart&&localEnd&&localCandidate?localStart:start,edgeEnd=localStart&&localEnd&&localCandidate?localEnd:end,edgeCandidate=localStart&&localEnd&&localCandidate?localCandidate:candidate.point;
        const edgeDirection=subtract(edgeEnd,edgeStart),edgeLengthSquared=dot(edgeDirection,edgeDirection),delta=subtract(edgeCandidate,edgeStart),parameter=dot(delta,edgeDirection)/edgeLengthSquared;
        if(parameter<=epsilon/Math.sqrt(edgeLengthSquared)||parameter>=1-epsilon/Math.sqrt(edgeLengthSquared))continue;
        if(Math.hypot(...delta.map((value,axis)=>value-edgeDirection[axis]*parameter))<=epsilon)interior.push({index:candidate.index,parameter});
      }
      interior.sort((left,right)=>left.parameter-right.parameter);for(const candidate of interior)if(boundary.at(-1)!==candidate.index)boundary.push(candidate.index);split||=interior.length>0;
    }
    if(split){const center=coordinates.length/3;coordinates.push(...[0,1,2].map(axis=>polygon.reduce((sum,index)=>sum+coordinates[index*3+axis],0)/polygon.length));components.push(components[polygon[0]]);for(let index=0;index<boundary.length;index++)render.push(center,boundary[index],boundary[(index+1)%boundary.length]);}
    else if(polygon.length===4)render.push(polygon[0],polygon[1],polygon[2],polygon[0],polygon[2],polygon[3]);else render.push(...polygon);
  }
  const outputPositions=Float32Array.from(coordinates),renderIndices=Uint32Array.from(render);
  const originalTopology=triangleTopology(positions,indices),outputTopology=triangleTopology(outputPositions,renderIndices);
  if(!outputTopology.closed||outputTopology.euler!==originalTopology.euler||[...outputTopology.degenerate].some(triangle=>!originalTopology.degenerate.has(triangle)))return {positions:Float32Array.from(positions),...mixedMeshPatches(positions,indices,faceIds),planarPartitions:{faces:0,holes:0,localFrames:0,rejectedFaces:partitions.length,warning:'局部分块未通过共享边或拓扑检查，保留原合法网格'}};
  return {positions:outputPositions,quadIndices:Uint32Array.from(quads.flat()),triangleIndices:Uint32Array.from(triangles.flat()),renderIndices,quadCount:quads.length,trianglePatchCount:triangles.length,equivalentTriangleCount:quads.length*2+triangles.length,planarPartitions:{faces:partitions.length,holes:partitions.reduce((sum,{partition})=>sum+partition.holeCount,0),localFrames:partitions.reduce((sum,{partition})=>sum+partition.frames.length,0),...precisionEvidence}};
}
