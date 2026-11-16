import {createProject,validateProject,geometrySignature} from './Core/project.mjs';
import {buildOriginalDesignUpstream} from './Core/original-design-upstream.mjs';
import {createOriginalCabinetInput,originalCabinetScenario} from './Core/original-cabinet.mjs';
import {cabinetScenePath} from './Core/cabinet-binding.mjs';
import {nativeSceneToPreview} from './Core/structure-installation-preview.mjs';
import {inverseRigidMatrix,multiplyMatrix} from './Core/geometry.mjs';
import {applyOriginalJointResult} from './Core/original-joint-design.mjs';
import {installOriginalJointHangers} from './Core/original-joint-hangers.mjs';
import {collectOriginalRoofPlanes} from './Core/original-roof-planes.mjs';
import {deriveCabinetScene} from './Core/cabinet-scene.mjs';
import {structurePreviewPlacements} from './Core/structure-preview-placement.mjs';
import cabinetSources from './catalog/cabinet-scene-sources.json' with {type:'json'};
import {objectDimensionBinding,prepareBusinessDimensionEdit} from './Core/business-dimensions.mjs';
import {transformPoint} from './Core/geometry.mjs';
import packageManifest from './runtime-manifest.json' with {type:'json'};
import {createKernelFromModule} from './Web/src/kernel.js';
import {measureFixedStructureClearance} from './Core/fixed-structure-clearance.mjs';
import {fixedHangerDatumDefinition,hangerTemplatePath} from './Core/hanger-installation.mjs';
import {facingRegularFrameCasings} from './Core/regular-frame-casing.mjs';

const calculatorVersion=packageManifest.calculatorVersion+':'+packageManifest.packageSha256;
const definitions=new Map(),shapeCache=new Map();let shapeBytes=0;
let roofCache;
const maxShapeBytes=192*1024*1024;
async function read(path){
  if(path===hangerTemplatePath)return fixedHangerDatumDefinition();
  if(!path||path.includes('..')||path.includes(':')||path.startsWith('/'))throw new Error('模板相对身份无效：'+path);
  if(!definitions.has(path)){
    const pending=(async()=>{const response=await fetch('./templates/'+path+'.virtual.json');if(!response.ok)throw new Error('离线模板定义缺失：'+path);const definition=await response.json();if(definition.source?.path!==path)throw new Error('缓存定义身份不匹配：'+path);return definition;})();
    definitions.set(path,pending);pending.catch(()=>{if(definitions.get(path)===pending)definitions.delete(path);});
  }
  return definitions.get(path);
}
const kernelModule=fetch('./vendor/manifold/manifold.wasm').then(response=>{if(!response.ok)throw new Error('Manifold计算内核缺失');return response.arrayBuffer();}).then(WebAssembly.compile);
const upstreamKernel=kernelModule.then(createKernelFromModule);
upstreamKernel.then(()=>self.postMessage({type:'ready'}),error=>self.postMessage({type:'fatal',ok:false,error:error.message}));
const managedRequests=new Map();let managedSequence=0;
function managed(method,input){return new Promise((resolve,reject)=>{const managedId=++managedSequence;managedRequests.set(managedId,{resolve,reject});self.postMessage({type:'managed-request',managedId,method,input});});}
const geometry=new Worker('./Web/src/geometry-worker.js',{type:'module'}),jobs=new Map();let geometrySequence=0;
kernelModule.then(module=>geometry.postMessage({type:'initialize-kernel',module}),error=>geometry.postMessage({type:'initialize-kernel-error',message:error.message}));
geometry.onmessage=event=>{const pending=jobs.get(event.data.requestId);if(!pending)return;jobs.delete(event.data.requestId);event.data.ok?pending.resolve(event.data):pending.reject(Object.assign(new Error(event.data.error),{source:event.data.source,detail:event.data.detail}));};
geometry.onerror=event=>{for(const pending of jobs.values())pending.reject(new Error(event.message));jobs.clear();self.postMessage({type:'fatal',ok:false,error:event.message});};
const hash=text=>{let value=2166136261;for(let index=0;index<text.length;index++)value=Math.imul(value^text.charCodeAt(index),16777619);return (value>>>0).toString(16);};
function evaluate(object,settings,casing){
  // Full canonical inputs verify a hash hit. Revision, installation and styling are absent.
  const canonical=geometrySignature(object)+JSON.stringify([calculatorVersion,settings.arcSegments,settings.complexArcSegments,settings.chordToleranceMm,settings.objectTriangleBudget,casing]);
  const key=hash(canonical),cached=shapeCache.get(key);
  if(cached?.canonical===canonical){shapeCache.delete(key);shapeCache.set(key,cached);return Promise.resolve(cached.result);}
  return new Promise((resolve,reject)=>{
    const requestId=++geometrySequence;
    jobs.set(requestId,{resolve:result=>{
      const bytes=result.meshes.reduce((sum,mesh)=>sum+mesh.positions.byteLength+mesh.indices.byteLength+(mesh.outlinePositions?.byteLength??0)+(mesh.outlineTriangles?.byteLength??0)+mesh.patches.quads.byteLength+mesh.patches.triangles.byteLength,0);
      if(cached){shapeCache.delete(key);shapeBytes-=cached.bytes;}
      if(bytes<=maxShapeBytes){while(shapeBytes+bytes>maxShapeBytes&&shapeCache.size){const oldest=shapeCache.keys().next().value;shapeBytes-=shapeCache.get(oldest).bytes;shapeCache.delete(oldest);}shapeCache.set(key,{canonical,result,bytes});shapeBytes+=bytes;}
      resolve(result);
    },reject});
    geometry.postMessage({requestId,revision:0,object,settings,signature:canonical,purpose:'native-sw',casing});
  });
}
function at(source,path){return path.split('.').reduce((object,key)=>object?.[key],source);}
function sceneReferenceId(sceneId,componentChain){return sceneId+':reference:'+JSON.stringify(componentChain);}
function inputs(request){
  const record=createOriginalCabinetInput(request.scenario),definition=originalCabinetScenario(request.scenario);
  for(const [path,initial]of Object.entries(definition.defaults)){
    let value=at(request.parameters,path);
    if(value===undefined)throw new Error('原界面参数快照缺失字段：'+path);
    // Newtonsoft serializes enums as names; cached UI contracts use their original names.
    if(typeof initial==='number'&&path!=='General.InsulationDistance')value=Math.fround(value);
    if(typeof initial==='string'&&typeof value!=='string')value=String(value);
    record.values[path]=value;
  }
  record.orientation=request.orientation??request.parameters.Orientation??'CabinetFrontToWall';
  return record;
}
async function readDraftRoof(project,source){
  const record=project.cabinetInputs[project.scenario],key=JSON.stringify([source.path,source.configuration,record]);
  if(roofCache?.key===key)return roofCache.result;
  const result=(async()=>{
    try{
      const derived=await deriveCabinetScene(await read(source.path),source.configuration,project.scenario,record,read);
      const metadata=await collectOriginalRoofPlanes(derived,source.configuration);
      if(!metadata.complete||!metadata.planes.length)return {problem:'柜体模板缺少可核验的横梁下表面。'};
      return {identity:JSON.stringify([source.path,source.configuration,metadata.selectedComponent]),
        faces:metadata.planes.map(({point,normal})=>({point,normal})),metadata};
    }catch(error){return {problem:'读取柜体模板横梁失败：'+error.message};}
  })();
  roofCache={key,result};return result;
}
async function prepare(request){
  let project=createProject(request.scenario);project.id=request.sessionId;project.name=request.modelName;
  project.revision=request.revision;project.cabinetInputs={[request.scenario]:inputs(request)};
  project.settings={...project.settings,...request.settings,captureLocalMeshes:true,includeCopperHoles:true};project.designModules={cabinet:true};
  const path=cabinetScenePath(project.scenario,project.cabinetInputs[project.scenario]),source=cabinetSources.models.find(row=>row.path===path);
  if(!source)throw new Error('场景没有已核验的缓存模板身份');
  project.objects=[{id:request.sessionId+':cabinet',name:'原柜体及环境',category:'cabinet',parentId:null,templatePath:path,configuration:source.configuration,sourceSha256:source.sha256,parameters:{},suppression:{},transform:[...nativeSceneToPreview],visible:true,cabinetBinding:{version:'original-cabinet-scene/1',scenario:project.scenario}}];
  project=validateProject(project);
  const upstream=await buildOriginalDesignUpstream(project,read,{kernel:await upstreamKernel,readWallConductors:input=>managed('wall-conductors',input)});
  const {metadata:roofPlanes,...hangerRoof}=await readDraftRoof(project,source);
  upstream.hangerRoof=hangerRoof;
  if(request.bridge){
    applyOriginalJointResult(project,{upstream,commands:[],result:request.bridge});
    project=validateProject(project);
    project=await installOriginalJointHangers(project,read,{roofPlanes});
  }
  const results=new Map();
  for(const object of project.objects)results.set(object.id,await evaluate(object,project.settings));
  const installed=structurePreviewPlacements(project,results);
  if(installed.diagnostics.length)throw new Error(installed.diagnostics.map(row=>row.message).join('；'));
  for(const assignment of installed.assignments)project.objects.find(row=>row.id===assignment.objectId).transform=assignment.transform;
  const regularBridge=request.bridge&&project.scenario==='face-to-face'&&project.frameDesign?.plans.length&&
    project.frameDesign.plans.every(frame=>frame.model.templateIdentity==='Regular024');
  const casings=regularBridge?facingRegularFrameCasings(project.objects,results):new Map();
  for(const object of project.objects)if(casings.has(object.id))
    results.set(object.id,await evaluate(object,project.settings,casings.get(object.id)));
  const triangleTotal=()=>[...results.values()].reduce((sum,result)=>sum+result.meshes.reduce((count,mesh)=>count+mesh.triangleCount,0),0);
  // The shared scene budget refines complex faces before falling back to the existing contour display.
  if(triangleTotal()>project.settings.sceneTriangleBudget){
    const perObject=Math.max(1,Math.floor(project.settings.sceneTriangleBudget/project.objects.length));
    for(const object of project.objects){const result=results.get(object.id);if(result.preview?.complexFeatureCount&&result.preview.triangleCount>perObject)
      results.set(object.id,await evaluate(object,{...project.settings,objectTriangleBudget:Math.min(project.settings.objectTriangleBudget,perObject)},casings.get(object.id)));}
  }
  const clearance=(request.checkClearance===true||regularBridge)&&request.bridge
    ?measureFixedStructureClearance(await upstreamKernel,project.objects,results,
      project.cabinetInputs[project.scenario].values['General.InsulationDistance']):null;
  const instances=[],interactionInstances=[],warnings=[],dimensions=[],web=request.renderer==='webgl';
  if(clearance&&!clearance.passed)warnings.push('绝缘净距不满足 '+clearance.requiredMm+
    ' mm：相间 '+clearance.phases.minimumMm.toFixed(2)+' mm，母线对金属 '+clearance.grounded.minimumMm.toFixed(2)+' mm');
  if(clearance?.assembly.count)warnings.push('检测到 '+clearance.assembly.count+' 处实体装配干涉：'+
    clearance.assembly.largest[0].parts.join(' ↔ '));
  const contourObjects=new Set();
  let solidTriangles=triangleTotal();
  for(const object of [...project.objects].sort((left,right)=>results.get(right.id).preview.triangleCount-results.get(left.id).preview.triangleCount)){
    const result=results.get(object.id);if(result.previewDisplayMode==='outline'){solidTriangles-=result.preview.triangleCount;continue;}
    if(solidTriangles>project.settings.sceneTriangleBudget){contourObjects.add(object.id);solidTriangles-=result.preview.triangleCount;}
  }
  if(contourObjects.size)warnings.push('场景超过公共面片预算，超限对象保留轮廓，其余对象继续显示实体');
  for(const object of project.objects){
    const result=results.get(object.id),pose=multiplyMatrix(inverseRigidMatrix(nativeSceneToPreview),object.transform);
    const outline=contourObjects.has(object.id),known=request.knownShapes??{};
    instances.push(...meshInstances(object,result,pose,known,outline,web));if(result.qualityWarning)warnings.push(result.qualityWarning);
    if(!web){
      let interaction=result;
      if(result.preview?.complexFeatureCount&&!outline){
        const candidate=await evaluate(object,{...project.settings,complexArcSegments:12,chordToleranceMm:project.settings.chordToleranceMm*4,
          objectTriangleBudget:Math.max(1,Math.floor(project.settings.objectTriangleBudget/4))},casings.get(object.id));
        if(candidate.preview.topologySignature===result.preview.topologySignature&&candidate.preview.triangleCount<result.preview.triangleCount)interaction=candidate;
      }
      interactionInstances.push(...meshInstances(object,interaction,pose,known,outline,false));
    }
    for(const parameter of result.parameters??[]){
      const binding=objectDimensionBinding(project,object.id,parameter.name);
      if(binding?.kind!=='cabinet'||!Number.isFinite(parameter.value)||!parameter.annotationEdges?.length)continue;
      let editable=false;
      try{editable=prepareBusinessDimensionEdit(project,binding).fields.some(field=>field.name===parameter.name);}catch(error){if(!/可输入|未开放|不属于/.test(error.message))throw error;}
      if(object.cabinetBinding&&!parameter.annotationComponentChain?.length)throw new Error('原柜体场景尺寸缺少组件身份：'+binding.field);
      const ownerId=object.cabinetBinding?sceneReferenceId(object.id,parameter.annotationComponentChain):object.id;
      dimensions.push({objectId:ownerId,field:binding.field,label:parameter.label,value:Math.fround(parameter.value),editable,
        edges:parameter.annotationEdges.map(edge=>edge.map(point=>transformPoint(point,pose).map(Math.fround)))});
    }
  }
  return {project,upstream,instances,interactionInstances,dimensions,clearance,warning:warnings.join('；')};
}
// Only real crease/boundary edges are emitted, never triangulation diagonals.
function contour(mesh){
  const shared=new Map(),positions=mesh.outlinePositions??mesh.positions,triangles=mesh.outlineTriangles??mesh.indices;
  const collapsed=[];
  const root=edge=>{if(edge.parent!==edge)edge.parent=root(edge.parent);return edge.parent;};
  const vertex=index=>[positions[index*3],positions[index*3+1],positions[index*3+2]];
  for(let offset=0;offset<triangles.length;offset+=3){
    const ids=Array.from(triangles.slice(offset,offset+3)),points=ids.map(vertex),u=points[1].map((v,i)=>v-points[0][i]),v=points[2].map((v,i)=>v-points[0][i]);
    const normal=[u[1]*v[2]-u[2]*v[1],u[2]*v[0]-u[0]*v[2],u[0]*v[1]-u[1]*v[0]],length=Math.hypot(...normal);
    if(!Number.isFinite(length))throw new Error('预览网格法向含非法坐标');
    if(length)normal.forEach((value,index)=>normal[index]=value/length);
    const edges=[];
    for(let side=0;side<3;side++){
      const a=ids[side],b=ids[(side+1)%3],key=a<b?a+':'+b:b+':'+a;
      let edge=shared.get(key);if(!edge){edge={a,b,normals:[]};edge.parent=edge;shared.set(key,edge);}if(length)edge.normals.push(normal);edges.push(edge);
    }
    if(!length)collapsed.push(edges);
  }
  // Manifold's valid Boolean output may contain collinear compatibility triangles along a split edge.
  // They carry adjacency, not surface area: join their three edge records so their real neighbours determine the contour.
  for(const edges of collapsed)for(const edge of edges.slice(1))root(edge).parent=root(edges[0]);
  const groups=new Map();for(const edge of shared.values()){const key=root(edge);if(!groups.has(key))groups.set(key,[]);groups.get(key).push(...edge.normals);}
  return [...shared.values()].filter(edge=>{const normals=groups.get(root(edge));return normals.length===1||normals.some(normal=>normal.reduce((sum,value,index)=>sum+value*normals[0][index],0)<Math.cos(Math.PI/6));}).flatMap(edge=>[edge.a,edge.b]);
}
function meshInstances(object,result,pose,known,outline,web){
  return result.meshes.map((mesh,index)=>{
    const source=mesh.partSource??{},installed=source.transform?multiplyMatrix(pose,source.transform):pose;
    const edgePositions=Array.from(mesh.outlinePositions??mesh.positions,Math.fround),edges=contour(mesh);
    for(const [first,second] of mesh.bendEdges??[]){
      const start=edgePositions.length/3;
      edgePositions.push(...first.map(Math.fround),...second.map(Math.fround));edges.push(start,start+1);
    }
    // WebGL uses the closed triangulation that already resolves shared edges.
    // Re-splitting the OpenGL quads here would reintroduce cracks at hole patches.
    const faces=web?{triangles:Array.from(mesh.indices)}:
      {quads:Array.from(mesh.patches.quads),triangles:Array.from(mesh.patches.triangles)};
    const geometry={positions:Array.from(mesh.positions,Math.fround),...faces,edgePositions,edges};
    // GPU shape inputs are body-local and verified in full. Parent component pose and appearance are independent instances.
    const signature=JSON.stringify([calculatorVersion,source.path,source.configuration,source.sha256,source.shapeInputSignature,geometry]),key=hash(signature);
    const shape=known[key]===signature?{}:geometry;
    const binding=object.originalDesignBinding;
    // Explicit original design bindings own bridge identity. References retain the exact exported component chain;
    // neither screen position nor a display-name search is used to guess an editor object.
    const designObjectId=binding?.rowId??binding?.objectId??(object.cabinetBinding?sceneReferenceId(object.id,source.componentChain??[]):object.id);
    if(!designObjectId)throw new Error('网格缺少设计对象身份：'+object.id);
    const category=object.cabinetBinding&&source.phase?'copper':object.category;
    return {id:object.id+':'+index,designObjectId,groupId:binding?.rowId??object.id,memberIds:binding?.memberIds??[designObjectId],displayName:source.componentChain?.length?source.componentChain.join(' / '):object.name,sourcePath:source.path??object.templatePath,geometryKey:key,geometrySignature:signature,category,appearanceRole:source.phase?'copper':source.appearanceRole??null,phase:object.phase??mesh.partSource?.phase??null,
      transform:installed.map(Math.fround),...shape,outlineOnly:outline||result.previewDisplayMode==='outline',partSource:mesh.partSource};
  });
}
let latest=null,running=false;
self.onmessage=event=>{
  if(event.data.type==='managed-response'){
    const pending=managedRequests.get(event.data.managedId);if(!pending)return;managedRequests.delete(event.data.managedId);
    event.data.ok?pending.resolve(event.data.result):pending.reject(new Error(event.data.error));return;
  }
  if(latest)self.postMessage({requestId:latest.requestId,ok:false,error:'参数修订已被较新输入替代'});
  latest=event.data;if(!running)drain();
};
async function drain(){
  running=true;
  while(latest){const request=latest;latest=null;try{
    const result=await prepare(request);
    if(!latest)self.postMessage({requestId:request.requestId,sessionId:request.sessionId,revision:request.revision,calculatorVersion,ok:true,...result});
    else self.postMessage({requestId:request.requestId,ok:false,error:'参数修订已被较新输入替代'});
  }catch(error){self.postMessage({requestId:request.requestId,sessionId:request.sessionId,revision:request.revision,ok:false,error:error.message,source:error.source,detail:error.detail??error.stack});}}
  running=false;
}
