import {DefinitionError} from './expressions.mjs';
import {meshFaceLabels} from './planar-mesh.mjs';
import {transformPoint} from './geometry.mjs';
const sub=(a,b)=>a.map((value,index)=>value-b[index]),cross=(a,b)=>[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]],dot=(a,b)=>a.reduce((sum,value,index)=>sum+value*b[index],0);

// Transient boundary DTO for the original analytic wall-conductor reader.
// Preserve each constructed body's face identity; no body is selected by box,
// order or volume. Nonplanar faces do not enter its planar-face recognition.
export function upstreamPlanarBodies(solids,placement){
  return solids.map(solid=>{
    const mesh=solid.getMesh(),labels=meshFaceLabels(mesh);if(!labels)throw new DefinitionError('套管实体缺少构造面的来源身份');
    const vertex=index=>transformPoint(Array.from(mesh.vertProperties.slice(index*mesh.numProp,index*mesh.numProp+3)),placement),groups=new Map();
    for(let triangle=0;triangle<labels.length;triangle++){const label=labels[triangle];if(!groups.has(label))groups.set(label,[]);groups.get(label).push(Array.from(mesh.triVerts.slice(triangle*3,triangle*3+3)));}
    const faces=[];
    for(const [label,triangles]of groups){
      const first=triangles.find(triangle=>Math.hypot(...cross(sub(vertex(triangle[1]),vertex(triangle[0])),sub(vertex(triangle[2]),vertex(triangle[0]))))>1e-8);if(!first)continue;
      const origin=vertex(first[0]),raw=cross(sub(vertex(first[1]),origin),sub(vertex(first[2]),origin)),normal=raw.map(value=>value/Math.hypot(...raw));
      if(triangles.some(triangle=>triangle.some(index=>Math.abs(dot(normal,sub(vertex(index),origin)))>.005)))continue;
      const boundary=new Map();for(const triangle of triangles)for(let corner=0;corner<3;corner++){const a=triangle[corner],b=triangle[(corner+1)%3],key=a<b?a+':'+b:b+':'+a;if(boundary.has(key))boundary.delete(key);else boundary.set(key,[a,b]);}
      const edges=[...boundary.values()].map(pair=>pair.map(vertex)).filter(([a,b])=>Math.hypot(...sub(a,b))>1e-6);
      if(edges.length)faces.push({identity:label,point:origin,normal,edges});
    }
    return {faces};
  });
}
