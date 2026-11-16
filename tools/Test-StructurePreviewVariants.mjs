import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {createKernelFromModule} from '../CKBusbarBridge.DesignUI/PreviewRuntime/Web/src/kernel.js';
import {deriveFixedStructureDatums,generateFromFixedDatums} from '../CKBusbarBridge.DesignUI/PreviewRuntime/Core/fixed-structure-datums.mjs';
import {baseTemplatePaths,frameTemplatePaths} from '../CKBusbarBridge.DesignUI/PreviewRuntime/Core/structure-sources.mjs';
import {makeBlock} from '../CKBusbarBridge.DesignUI/PreviewRuntime/Core/fixed-structure-templates.mjs';
import {swMatrix,inverseRigidMatrix} from '../CKBusbarBridge.DesignUI/PreviewRuntime/Core/geometry.mjs';

const runtime=new URL('../CKBusbarBridge.DesignUI/PreviewRuntime/',import.meta.url);
const kernel=await createKernelFromModule(await WebAssembly.compile(await readFile(new URL('vendor/manifold/manifold.wasm',runtime))));
const definitions=new Map();
function read(path){
  if(!definitions.has(path))definitions.set(path,readFile(new URL('templates/'+path+'.virtual.json',runtime),'utf8').then(JSON.parse));
  return definitions.get(path);
}
const cases=[
  {kind:'FaceToFaceSmallOffset032',variables:{'基架宽度':1100,'基架长度':1000,'基架高度':500,
    '进孔偏移方向':0,'进孔偏移尺寸':50,'出孔偏移方向':1,'出孔偏移尺寸':50}},
  {kind:'FaceToFaceLargeOffsetCorner028',variables:{'基架长度':820,'基架宽度':1000,'基架高度':500}},
  {kind:'SideToSidePhaseChange029',variables:{'基架长度':1003,'基架宽度':500,'基架高度':800}},
  {kind:'FaceToWallLarge1',variables:{'基架宽度':1160,'基架长度':1400,'基架高度':700,'上侧封板宽度':100,'下侧封板宽度':80}},
  {kind:'FaceToWallLarge2',variables:{'基架宽度':1000,'基架长度':1320,'基架高度':820,'侧盖板宽度':130}},
  {kind:'Base1',variables:{'基座高':1330}}, {kind:'Base2',variables:{'基座高':1040}}, {kind:'ElevatedBase',variables:{'高度':510}}
];

for(const {kind,variables} of cases){
  const isFrame=Object.hasOwn(frameTemplatePaths,kind),definition=await read((isFrame?frameTemplatePaths:baseTemplatePaths)[kind]);
  const configurationName=definition.configurations[0].name;
  const derived=await deriveFixedStructureDatums(definition,configurationName,{
    globalVariables:variables,
    rootDimensions:isFrame?{'D1@草图3':300}:{},
    businessDrive:kind==='FaceToFaceSmallOffset032'?{kind,suppressTemplateAngleNotches:null}:null
  },read);
  const {solids,solidSources}=generateFromFixedDatums(kernel,derived);
  try{
    assert.ok(solids.every(solid=>solid.status()==='NoError'&&!solid.isEmpty()),kind+' 包含无效实体');
    const names=solidSources.map(source=>source.componentChain[0]);
    if(isFrame){
      assert.ok(names.includes('基架盖板')&&names.includes('基架底板'),kind+' 缺少顶板或底板');
      assert.equal(names.includes('基架左侧板'),kind!=='FaceToFaceLargeOffsetCorner028');
      assert.ok(names.includes(kind==='FaceToFaceLargeOffsetCorner028'?'拐角封闭端侧板':'基架右侧板'));
    }else assert.equal(names.includes('基座顶盖'),kind!=='ElevatedBase',kind+' 顶部封板方向错误');
    const roofIndex=names.indexOf(isFrame?'基架盖板':'基座顶盖');
    if(roofIndex>=0){
      const roof=derived.shapeToNative?solids[roofIndex].transform(inverseRigidMatrix(derived.shapeToNative)):solids[roofIndex];
      try{assert.ok(Math.abs(roof.boundingBox().max[1]-derived.box.max[1])<.001,kind+' 盖板外表面偏离控制高度');}
      finally{if(derived.shapeToNative)roof.delete();}
    }
    const portNames=isFrame?['坐标系-进1','坐标系-出1']:kind==='ElevatedBase'?['上','下']:['基座安装坐标系'];
    const ports=derived.template.configurations[0].features.filter(row=>!row.suppressed&&portNames.includes(row.name));
    assert.equal(ports.length,isFrame||kind==='ElevatedBase'?2:1);
    for(const port of ports){
      const point=swMatrix(port.definition.properties.Transform.transform).slice(12,15);
      const probe=makeBlock(kernel,point.map(value=>value-.25),point.map(value=>value+.25));
      try{
        for(const [index,solid] of solids.entries()){
          const overlap=solid.intersect(probe);
          try{assert.ok(overlap.isEmpty()||overlap.volume()<1e-6,kind+' '+port.name+' 被 '+names[index]+' 封死');}
          finally{overlap.delete();}
        }
      }finally{probe.delete();}
    }
    for(let a=0;a<solids.length;a++)for(let b=a+1;b<solids.length;b++){
      const overlap=solids[a].intersect(solids[b]);
      try{
        if(overlap.isEmpty())continue;
        const {min,max}=overlap.boundingBox();
        assert.ok(max.some((high,axis)=>high-min[axis]<=.001)||Math.abs(overlap.volume())<1e-6,
          kind+' 板件相交：'+names[a]+' / '+names[b]);
      }finally{overlap.delete();}
    }
    console.log(kind+'：'+solids.length+' 个实体，接口通畅，结构件无干涉。');
  }finally{solids.forEach(solid=>solid.delete());}
}
