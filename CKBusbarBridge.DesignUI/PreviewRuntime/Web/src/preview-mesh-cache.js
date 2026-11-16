import {solidToMesh} from '../../Core/geometry.mjs';
import {meshFaceLabels} from '../../Core/planar-mesh.mjs';
const bytesOf=array=>array?new Uint8Array(array.buffer,array.byteOffset,array.byteLength):new Uint8Array();
const equalBytes=(left,right)=>{if(left.length!==right.length)return false;for(let index=0;index<left.length;index++)if(left[index]!==right[index])return false;return true;};

// Cache the pure mesh conversion, never a mutable Manifold solid. Hash matches
// are verified against all input bytes, including the original face grouping.
export class PreviewMeshCache{
  constructor(maxBytes=16*1024*1024){this.maxBytes=maxBytes;this.entries=new Map();this.bytes=0;this.hits=0;this.misses=0;}
  convert(solid){
    const native=solid.getMesh(),labels=meshFaceLabels(native),origin=Array.from(native.vertProperties.slice(0,3)),localProperties=Float64Array.from(native.vertProperties,(value,index)=>index%native.numProp<3?value-origin[index%native.numProp]:value),inputs=[localProperties,native.triVerts,labels].map(bytesOf);let hash=2166136261;
    for(const input of inputs){hash=Math.imul(hash^input.length,16777619);for(const value of input)hash=Math.imul(hash^value,16777619);}
    const key=native.numVert+':'+native.numProp+':'+hash,prior=this.entries.get(key);
    if(prior&&inputs.every((input,index)=>equalBytes(input,prior.inputs[index]))){
      const mesh=structuredClone(prior.mesh),delta=origin.map((value,index)=>value-prior.origin[index]);let exact=true;
      if(delta.some(value=>value!==0))for(const positions of [mesh.positions,mesh.outlinePositions].filter(Boolean))for(let index=0;index<positions.length;index++){const shifted=Math.fround(positions[index]+delta[index%3]);if(shifted-positions[index]!==delta[index%3]){exact=false;break;}positions[index]=shifted;}
      if(exact){this.hits++;this.entries.delete(key);this.entries.set(key,prior);return {...mesh,volume:solid.volume()};}
    }
    this.misses++;const mesh=solidToMesh({getMesh:()=>native,volume:()=>solid.volume()}),stored=structuredClone(mesh),copies=inputs.map(input=>input.slice());
    const size=copies.reduce((sum,array)=>sum+array.byteLength,0)+[stored.positions,stored.indices,stored.outlinePositions,stored.outlineTriangles,stored.patches.quads,stored.patches.triangles].reduce((sum,array)=>sum+(array?.byteLength??0),0);
    if(prior){this.entries.delete(key);this.bytes-=prior.size;}
    if(size<=this.maxBytes){while(this.bytes+size>this.maxBytes&&this.entries.size){const oldest=this.entries.keys().next().value;this.bytes-=this.entries.get(oldest).size;this.entries.delete(oldest);}this.entries.set(key,{inputs:copies,mesh:stored,origin,size});this.bytes+=size;}
    return mesh;
  }
  snapshot(){return {hits:this.hits,misses:this.misses,bytes:this.bytes,entries:this.entries.size};}
}
