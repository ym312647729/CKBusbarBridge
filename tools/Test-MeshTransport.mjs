import assert from 'node:assert/strict';
import {compactMeshResponse} from '../CKBusbarBridge.DesignUI/PreviewRuntime/mesh-transport.mjs';

const first={id:'第一实例',geometryKey:'形状一',geometrySignature:'完整签名一',phase:'A',
  positions:[0,-0,Math.fround(1/3),Math.fround(-123456.789),Math.fround(1e-40),3.4028234663852886e38],
  edgePositions:[-2,0,2.5],quads:[0,1,2,3],triangles:[],edges:[0,4294967295]};
const response={instances:[structuredClone(first),{...structuredClone(first),id:'第二实例'}],
  interactionInstances:[{...structuredClone(first),id:'交互实例'}]};
const metrics=compactMeshResponse(response);
assert.equal(metrics.shapes,1);assert.equal(metrics.references,2);
assert.equal(response.instances[0].phase,'A');
for(const [field,encoded]of Object.entries(response.instances[0].packedGeometry.fields)){
  const bytes=Buffer.from(encoded,'base64'),floating=['positions','edgePositions'].includes(field);
  const decoded=Array.from({length:bytes.length/4},(_,index)=>floating?bytes.readFloatLE(index*4):bytes.readUInt32LE(index*4));
  assert.deepEqual(decoded,first[field],'二进制传输改变了原精度网格');
}
assert.equal(response.instances[1].positions,undefined);assert.equal(response.interactionInstances[0].positions,undefined);
const changed={instances:[structuredClone(first),{...structuredClone(first),geometrySignature:'不同签名'}]};
assert.equal(compactMeshResponse(changed).shapes,2,'不同签名不能作为重复网格省略');
for(const patch of [{positions:[NaN]},{positions:[1/3]},{edges:[-1]},{geometryKey:null}])
  assert.throws(()=>compactMeshResponse({instances:[{...structuredClone(first),...patch}]}),'非法传输应明确拒绝');
console.log('网格传输核查通过：原精度坐标、最大无符号索引、重复引用、签名差异和非法数据拒绝。');
