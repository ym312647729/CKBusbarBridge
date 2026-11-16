const subtract=(left,right)=>left.map((value,index)=>value-right[index]);
const cross=(left,right)=>[left[1]*right[2]-left[2]*right[1],left[2]*right[0]-left[0]*right[2],left[0]*right[1]-left[1]*right[0]];
const dot=(left,right)=>left.reduce((sum,value,index)=>sum+value*right[index],0);
const floatResolution=points=>{let scale=1;for(const point of points)for(const coordinate of point)scale=Math.max(scale,Math.abs(coordinate));return scale*2**-22;};

function rectangularFaces(triangles,candidates,point){
  const parents=triangles.map((_,index)=>index),find=index=>{while(parents[index]!==index){parents[index]=parents[parents[index]];index=parents[index];}return index;};
  for(const {first,second}of candidates){
    const left=triangles[first],right=triangles[second],origin=point(left.vertices[0]),points=[...left.vertices,...right.vertices].map(point);
    if(!Number.isFinite(left.normal[0])||Math.fround(dot(left.normal,right.normal))!==1||points.some(value=>Math.abs(dot(subtract(value,origin),left.normal))>floatResolution(points)))continue;
    parents[find(second)]=find(first);
  }
  const groups=new Map();for(let index=0;index<triangles.length;index++){const root=find(index);if(!groups.has(root))groups.set(root,[]);groups.get(root).push(index);}
  const results=[];
  for(const members of groups.values()){
    if(members.length<2)continue;
    const first=triangles[members[0]],origin=point(first.vertices[0]),points=members.flatMap(index=>triangles[index].vertices.map(point)),resolution=floatResolution(points);
    if(points.some(value=>Math.abs(dot(subtract(value,origin),first.normal))>resolution)||members.some(index=>Math.fround(dot(triangles[index].normal,first.normal))!==1))continue;
    const edges=new Map();let invalid=false;
    for(const index of members)for(let corner=0;corner<3;corner++){
      const vertices=triangles[index].vertices,from=vertices[corner],to=vertices[(corner+1)%3],key=from<to?from+':'+to:to+':'+from,prior=edges.get(key);
      if(prior){if(prior.from!==to||prior.to!==from){invalid=true;break;}edges.delete(key);}else edges.set(key,{from,to});
    }
    if(invalid||edges.size<4)continue;
    const next=new Map();for(const {from,to}of edges.values()){if(next.has(from)){invalid=true;break;}next.set(from,to);}
    if(invalid)continue;
    const boundary=[next.keys().next().value];
    while(boundary.length<=next.size&&next.get(boundary.at(-1))!==boundary[0]){const to=next.get(boundary.at(-1));if(to===undefined||boundary.includes(to)){invalid=true;break;}boundary.push(to);}
    // A hole produces a second loop. Never replace a multiply connected face.
    if(invalid||boundary.length!==next.size||next.get(boundary.at(-1))!==boundary[0])continue;
    const corners=[...boundary];let reduced=true;
    while(reduced&&corners.length>4){reduced=false;for(let index=0;index<corners.length;index++){
      const a=point(corners[(index+corners.length-1)%corners.length]),b=point(corners[index]),c=point(corners[(index+1)%corners.length]),direction=subtract(c,a),length=Math.hypot(...direction),delta=subtract(b,a),station=dot(delta,direction)/(length*length);
      if(length>0&&station>0&&station<1&&Math.hypot(...cross(delta,direction))/length<=resolution){corners.splice(index,1);reduced=true;break;}
    }}
    if(corners.length!==4||corners.some((index,corner)=>dot(cross(subtract(point(corners[(corner+1)%4]),point(index)),subtract(point(corners[(corner+2)%4]),point(corners[(corner+1)%4]))),first.normal)<=0))continue;
    results.push({members,corners});
  }
  return results;
}

// SolidWorks GL draws these quads directly. Compatibility triangles serve only
// WebGL, topology and picking; native face labels are not geometric creases.
export function mixedMeshPatches(positions,sourceIndices,faceIds){
  const point=index=>[positions[index*3],positions[index*3+1],positions[index*3+2]],triangles=[],edges=new Map(),candidates=[];
  for(let offset=0;offset<sourceIndices.length;offset+=3){
    const vertices=Array.from(sourceIndices.slice(offset,offset+3)),a=point(vertices[0]),normal=cross(subtract(point(vertices[1]),a),subtract(point(vertices[2]),a)),length=Math.hypot(...normal),index=triangles.length;
    triangles.push({vertices,normal:normal.map(value=>value/length),area: length/2});
    for(let corner=0;corner<3;corner++){
      const from=vertices[corner],to=vertices[(corner+1)%3],key=from<to?from+':'+to:to+':'+from,prior=edges.get(key);
      if(prior){if(prior.from===to&&prior.to===from)candidates.push({first:prior.triangle,second:index,from,to,lengthSquared:dot(subtract(point(from),point(to)),subtract(point(from),point(to)))});edges.delete(key);}
      else edges.set(key,{from,to,triangle:index});
    }
  }
  candidates.sort((left,right)=>right.lengthSquared-left.lengthSquared);
  const used=new Uint8Array(triangles.length),quads=[],remaining=[];
  // Collapse the complete uncut face, including a fan or collinear boundary
  // vertices. Pairing alone would leave two or more patches on one rectangle.
  for(const face of rectangularFaces(triangles,candidates,point)){quads.push(...face.corners);face.members.forEach(index=>used[index]=1);}
  for(const candidate of candidates){
    if(used[candidate.first]||used[candidate.second])continue;
    const first=triangles[candidate.first],second=triangles[candidate.second];if(!Number.isFinite(first.normal[0])||Math.fround(dot(first.normal,second.normal))!==1)continue;
    const boundary=new Map();for(const triangle of [first,second])for(let corner=0;corner<3;corner++){const from=triangle.vertices[corner],to=triangle.vertices[(corner+1)%3];if((from===candidate.from&&to===candidate.to)||(from===candidate.to&&to===candidate.from))continue;boundary.set(from,to);}
    if(boundary.size!==4)continue;
    const quad=[boundary.keys().next().value];for(let index=0;index<3;index++)quad.push(boundary.get(quad.at(-1)));
    if(new Set(quad).size!==4||quad.includes(undefined)||boundary.get(quad[3])!==quad[0])continue;
    const points=quad.map(point),tolerance=Math.max(first.area+second.area,1e-20)*1e-10;
    if(points.some((current,index)=>dot(cross(subtract(points[(index+1)%4],current),subtract(points[(index+2)%4],points[(index+1)%4])),first.normal)<=tolerance))continue;
    // Float32 roundoff budget only, not a business dimension tolerance. A true
    // crease cannot be merged just because it is below the 1 mm input tolerance.
    const planeError=Math.abs(dot(subtract(points[3],points[0]),first.normal));
    if(planeError>floatResolution(points))continue;
    // Retain the input diagonal in the compatibility triangles. Flipping it can
    // collide with a separate existing edge in sheets with coincident regions.
    const start=quad.indexOf(candidate.from);if(quad[(start+2)%4]!==candidate.to)continue;
    const ordered=quad.slice(start).concat(quad.slice(0,start));
    used[candidate.first]=1;used[candidate.second]=1;quads.push(...ordered);
  }
  for(let index=0;index<triangles.length;index++)if(!used[index])remaining.push(...triangles[index].vertices);
  const quadIndices=Uint32Array.from(quads),triangleIndices=Uint32Array.from(remaining);
  // Keep the exact closed source triangulation for WebGL/topology. These
  // indices are not submitted by the SolidWorks GL_QUADS drawing path.
  return {quadIndices,triangleIndices,renderIndices:Uint32Array.from(sourceIndices),quadCount:quads.length/4,trianglePatchCount:remaining.length/3,equivalentTriangleCount:quads.length/2+remaining.length/3};
}
