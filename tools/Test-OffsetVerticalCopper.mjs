import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {createKernelFromModule} from '../CKBusbarBridge.DesignUI/PreviewRuntime/Web/src/kernel.js';
import {createOriginalCopperObject,evaluateCopperObject,originalCopperVersion,planOriginalVerticalCopper,originalCopperSectionFrames} from '../CKBusbarBridge.DesignUI/PreviewRuntime/Core/original-copper.mjs';
import {planCopperPreviewHoles,cutCopperPreviewHoles} from '../CKBusbarBridge.DesignUI/PreviewRuntime/Core/original-copper-holes.mjs';

// 只运行小型纯几何回归，不连接 SolidWorks 或启动路径求解 Worker。
const runtime=new URL('../CKBusbarBridge.DesignUI/PreviewRuntime/',import.meta.url),
  kernel=await createKernelFromModule(await WebAssembly.compile(await readFile(new URL('vendor/manifold/manifold.wasm',runtime))));
const source={version:originalCopperVersion,isVerticalBend:true,isMilledBar:false,isCenter:true,coordinateFrame:'solidworks',pathContract:'native-material',
  widthVector:[0,0,1],startBusbarVector:[0,1,0],busBarWidth:80,busBarThickness:8,barBendingRadius:8,verticalBendingRadius:20,bendAllowance:20,
  pointList:[[0,0,0],[300,0,0],[350,0,50],[700,0,50]],previewBendPointKinds:[0,1,1,0]};
const rotate=point=>[point[2],point[1],-point[0]],add=(point,offset)=>point.map((value,axis)=>value+offset[axis]);
for(const scenario of ['面对面偏移','侧对侧偏移']){
  const setting=structuredClone(source);
  if(scenario==='侧对侧偏移'){
    setting.pointList=setting.pointList.map(rotate);setting.widthVector=rotate(setting.widthVector);setting.startBusbarVector=rotate(setting.startBusbarVector);
  }
  const before=JSON.stringify(setting),plan=planOriginalVerticalCopper(setting),copper=createOriginalCopperObject(setting,scenario,'A'),result=evaluateCopperObject(kernel,copper,{arcSegments:24});
  try{
    assert.equal(JSON.stringify(setting),before,'转换不能改写原输入');
    assert.equal(copper.phase,'A');assert.equal(result.solids.length,1);
    assert.deepEqual(plan.corners.map(corner=>corner.kind),['edge','edge']);
    assert.deepEqual(plan.corners.map(corner=>corner.innerRadiusMm),[20,20]);
    assert.equal(result.derivedGeometry.bends.length,2);
    const components=result.solids[0].decompose();
    try{assert.equal(components.length,1,'两处立弯和三条直段必须连续');}finally{components.forEach(component=>component.delete());}
    // 独立解析值：两处45°立弯，中心半径=20+80/2；圆弧按15°弦段近似。
    const centerRadius=60,angle=Math.PI/4,developed=650+Math.hypot(50,50)-4*centerRadius*Math.tan(angle/2)+2*centerRadius*angle,
      exactVolume=developed*80*8;
    assert.ok(Math.abs(result.solids[0].volume()-exactVolume)/exactVolume<.002,'体积应与实际折弯截面相符');
    const straightFrames=originalCopperSectionFrames(setting);
    assert.ok(straightFrames[0].length<300,'孔位直段必须扣除圆弧切线占长');
    copper.originalHolePlan=planCopperPreviewHoles(copper,{diameter:13,slotLength:13,style:'圆孔',location:'0,0'},[],4);
    assert.equal(copper.originalHolePlan.length,2);
    const volumeBefore=result.solids[0].volume(),placement=[...result.solidSources[0].transform];
    cutCopperPreviewHoles(kernel,result,copper,{arcSegments:24});
    assert.ok(result.solids[0].volume()<volumeBefore);
    assert.deepEqual(result.solidSources[0].transform,placement,'开孔不能改变安装定位');
    assert.ok(result.bendEdges.length>0,'圆弧的切入切出线不能丢失');
  }finally{result.solids.forEach(solid=>solid.delete());}
}
const mixed={...source,pointList:[[0,0,0],[150,0,0],[200,8,0],[350,8,0],[400,8,50],[650,8,50],[700,0,50],[850,0,50]]};
assert.deepEqual(planOriginalVerticalCopper(mixed).corners.map(corner=>corner.kind),['flat','flat','edge','edge','flat','flat']);
const rightAngle={...source,pointList:[[0,0,0],[300,0,0],[300,0,300],[600,0,300]]};
assert.equal(originalCopperSectionFrames(rightAngle).length,3,'90°立弯不能因原排宽与某段平行而拒绝');
const shifted={...source,pointList:source.pointList.map(point=>add(point,[500000,200000,-300000]))},
  shiftedObject=createOriginalCopperObject(shifted,'大坐标偏移排','B'),shiftedResult=evaluateCopperObject(kernel,shiftedObject,{arcSegments:24});
try{
  assert.ok(Math.max(...shiftedResult.solidSources[0].localMesh.positions.map(Math.abs))<1000,'几何必须先在局部计算，保留双精度安装平移');
  assert.deepEqual(shiftedResult.solidSources[0].transform.slice(12,15),[500000,300000,200000]);
}finally{shiftedResult.solids.forEach(solid=>solid.delete());}
assert.throws(()=>planOriginalVerticalCopper({...source,verticalBendingRadius:2000}),/长度不足/,'不得缩小半径绕过切线长度校验');
console.log('偏移立弯回归通过：面对面、侧对侧、平立混合、90°立弯、原孔位和局部安装坐标。');
