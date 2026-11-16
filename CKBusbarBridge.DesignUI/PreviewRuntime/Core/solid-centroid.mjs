import {DefinitionError} from './expressions.mjs';

// Signed tetrahedral moments of the constructed closed surface. No bounding
// box or captured validation/mass data is used as a positioning substitute.
export function solidCentroid(solids){
  let signedVolume6=0;const moment=[0,0,0];
  for(const solid of solids){
    const mesh=solid.getMesh(),vertices=mesh.vertProperties,stride=mesh.numProp;
    for(let index=0;index<mesh.triVerts.length;index+=3){
      const points=[0,1,2].map(offset=>{const vertex=mesh.triVerts[index+offset]*stride;return [vertices[vertex],vertices[vertex+1],vertices[vertex+2]];});
      const [a,b,c]=points,volume6=a[0]*(b[1]*c[2]-b[2]*c[1])+a[1]*(b[2]*c[0]-b[0]*c[2])+a[2]*(b[0]*c[1]-b[1]*c[0]);
      signedVolume6+=volume6;for(let axis=0;axis<3;axis++)moment[axis]+=(a[axis]+b[axis]+c[axis])*volume6;
    }
  }
  if(!Number.isFinite(signedVolume6)||signedVolume6<=1e-8)throw new DefinitionError('绝缘子实体缺少有效的闭合体积中心');
  return moment.map(value=>value/(4*signedVolume6));
}
