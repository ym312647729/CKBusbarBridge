import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {createKernelFromModule} from '../CKBusbarBridge.DesignUI/PreviewRuntime/Web/src/kernel.js';
import {measureFixedStructureClearance,mergeClearancePartitions} from '../CKBusbarBridge.DesignUI/PreviewRuntime/Core/fixed-structure-clearance.mjs';

const runtime=new URL('../CKBusbarBridge.DesignUI/PreviewRuntime/',import.meta.url);
const kernel=await createKernelFromModule(await WebAssembly.compile(await readFile(new URL('vendor/manifold/manifold.wasm',runtime))));
const identity=[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1],objects=[],results=new Map();
for(const [index,phase,x]of [[0,'A',0],[1,'B',8],[2,'C',180],[3,null,195]]){
  const id=String(index),solid=kernel.Manifold.cube([20,20,20]).translate([x,0,0]);
  try{
    const mesh=solid.getMesh();
    objects.push({id,name:phase??'接地板',phase,category:phase?'copper':'frame',transform:identity});
    results.set(id,{meshes:[{outlinePositions:mesh.vertProperties,outlineTriangles:mesh.triVerts}]});
  }finally{solid.delete();}
}
const expected=measureFixedStructureClearance(kernel,objects,results,125);
assert.equal(expected.passed,false);assert.ok(expected.phases.below>0&&expected.grounded.below>0);
assert.ok(expected.assembly.count>=2);
for(const count of [1,2,3,17]){
  const actual=mergeClearancePartitions(Array.from({length:count},(_,index)=>
    measureFixedStructureClearance(kernel,objects,results,125,{index,count})));
  assert.deepEqual(actual,expected,'并行分区漏检净距或干涉');
  const workQueue=new SharedArrayBuffer(Int32Array.BYTES_PER_ELEMENT*3);
  const shared=mergeClearancePartitions(Array.from({length:count},(_,index)=>
    measureFixedStructureClearance(kernel,objects,results,125,{index,count,workQueue})));
  assert.deepEqual(shared,expected,'动态领取任务漏检净距或干涉');
}
console.log('并行检查通过：含相间冲突、对地冲突、实体干涉及空分区；与单线程完全一致。');
