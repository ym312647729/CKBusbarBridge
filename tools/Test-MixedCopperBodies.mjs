import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {createKernelFromModule} from '../CKBusbarBridge.DesignUI/PreviewRuntime/Web/src/kernel.js';
import {createOriginalCopperObject,evaluateCopperObject,originalCopperVersion} from '../CKBusbarBridge.DesignUI/PreviewRuntime/Core/original-copper.mjs';
import {measureFixedStructureClearance} from '../CKBusbarBridge.DesignUI/PreviewRuntime/Core/fixed-structure-clearance.mjs';
import {cutCopperPreviewHoles} from '../CKBusbarBridge.DesignUI/PreviewRuntime/Core/original-copper-holes.mjs';

const runtime=new URL('../CKBusbarBridge.DesignUI/PreviewRuntime/',import.meta.url);
const kernel=await createKernelFromModule(await WebAssembly.compile(await readFile(new URL('vendor/manifold/manifold.wasm',runtime))));
const setting={version:originalCopperVersion,isVerticalBend:true,isMilledBar:false,isCenter:true,
  widthVector:[0,0,1],startBusbarVector:[0,1,0],busBarWidth:80,busBarThickness:8,
  barBendingRadius:8,verticalBendingRadius:8,bendAllowance:20,
  pointList:[[0,0,0],[300,0,0],[350,0,50],[700,0,50]],previewBendPointKinds:[0,1,1,0]};
const copper=createOriginalCopperObject(setting,'偏移排','A'),evaluated=evaluateCopperObject(kernel,copper);
try{
  assert.equal(evaluated.solids.length,1,'一根混合工艺排不能按直段拆成多个装配零件');
  const actual=evaluated.solids[0];
  assert.equal(evaluated.derivedGeometry.bends.length,2,'两处折弯必须生成真实圆弧');
  const objects=[],results=new Map(),identity=[1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1];
  for(const [id,phase,offset] of [['A1','A',0],['A2','A',0],['B','B',400],['C','C',800],['metal',null,1200]]){
    const solid=actual.translate([0,offset,0]);
    try{
      const mesh=solid.getMesh();objects.push({id,name:id,phase,category:phase?'copper':'frame',transform:identity});
      results.set(id,{meshes:[{outlinePositions:mesh.vertProperties,outlineTriangles:mesh.triVerts}]});
    }finally{solid.delete();}
  }
  const interference=measureFixedStructureClearance(kernel,objects,results,125);
  assert.equal(interference.assembly.count,1,'不同根同相铜排的真实重叠仍必须报出');
  assert.deepEqual(interference.assembly.largest[0].parts,['A1','A2']);
  const crossing=createOriginalCopperObject({...setting,widthVector:[0,1,0],startBusbarVector:[0,0,1],pointList:[[0,0,0],[200,0,0],[200,0,200],[100,0,200],[100,0,-100]],
    previewBendPointKinds:[0,1,1,1,0]},'自交排','A');
  assert.throws(()=>evaluateCopperObject(kernel,crossing),/自身相交|材料重叠/,'真实路径自交不能被合并掩盖');
  const originalVolume=actual.volume(),originalLocalMesh=evaluated.solidSources[0].localMesh,
    originalTransform=[...evaluated.solidSources[0].transform];
  copper.originalHolePlan=[{center:[100,0,0],normal:[0,0,1],axis:[1,0,0],diameter:13,slotLength:13}];
  cutCopperPreviewHoles(kernel,evaluated,copper,{arcSegments:24});
  assert.ok(evaluated.solids[0].volume()<originalVolume,'合并后的铜排仍必须真实开孔');
  assert.notEqual(evaluated.solidSources[0].localMesh,originalLocalMesh,'不能重用未开孔的局部网格');
  assert.deepEqual(evaluated.solidSources[0].transform,originalTransform,'开孔不能丢掉局部到安装坐标的变换');
  assert.ok(Math.abs(evaluated.solidSources[0].localMesh.volume-evaluated.solids[0].volume())<1e-5);
}finally{evaluated.solids.forEach(part=>part.delete());}
console.log('混合铜排检查通过：同一根连续圆弧实体、不同根同相重叠及路径自交仍拦截，开孔后局部网格和安装变换正确。');
