import * as THREE from './lib/three/three.module.js';
import {OcctCameraControls} from './camera-controls.js';
import {createCoordinateTriad} from './coordinate-triad.js';
import {createDimensionOverlay} from './dimensions.js';
import {formatParameter} from './parameter-numbers.js';
import {originalCabinetScenario,createOriginalCabinetInput,editOriginalCabinetUiInput} from './runtime/Core/original-cabinet.mjs';
import {cabinetFieldState} from './runtime/Core/business-dimensions.mjs';

const el=id=>document.getElementById(id),scenario='face-to-face',schema=originalCabinetScenario(scenario);
const sessionId=crypto.randomUUID(),pending=new Map(),message=el('report'),canvas=el('viewport'),stage=el('stage');
const worker=new Worker('./runtime/session-worker.mjs',{type:'module'});
let input=createOriginalCabinetInput(scenario),revision=0,clearanceMm=125,activeDimension,displayed=null,selected=null,isBusy=false;
let noticeTimer,workerReady;
const ready=new Promise(resolve=>workerReady=resolve);
function toast(text){const node=el('toast');node.textContent=text;node.hidden=false;clearTimeout(noticeTimer);noticeTimer=setTimeout(()=>node.hidden=true,5500);}
const paramFields=new Map(),fieldset=el('fields');
const grouping={'General':'整体参数','ACabinet':'A 柜','BCabinet':'B 柜'};
for(const [prefix,name] of Object.entries(grouping)){
  const details=document.createElement('details');details.open=true;
  const summary=document.createElement('summary');summary.textContent=name;details.append(summary);
  for(const field of schema.fields.filter(field=>field.path.startsWith(prefix+'.')&&!field.readOnly&&
    !field.path.endsWith('.InsulationDistance')&&!field.path.endsWith('.CabinetEccentricity'))){
    const original=schema.defaults[field.path],label=document.createElement('label');label.className='field';
    label.append(document.createTextNode(field.label.replace(/[:：]+$/,'')));
    const control=document.createElement(field.choices.length?'select':'input');
    control.dataset.field=field.path;
    if(field.choices.length)for(const choice of field.choices){
      const option=document.createElement('option');option.value=String(choice.value);option.textContent=choice.label??option.value;control.append(option);
    }
    else{control.type=typeof original==='boolean'?'checkbox':typeof original==='number'?'number':'text';control.step='any';}
    control.addEventListener('change',()=>{
      const value=typeof original==='boolean'?control.checked:
        typeof original==='number'?Number(control.value):control.value;
      if((control.type==='number'&&control.value==='')||typeof value==='number'&&!Number.isFinite(value)){refreshFields();return;}
      try{input=editOriginalCabinetUiInput(input,scenario,field.path,value);refreshFields();}
      catch(error){toast(error.message);refreshFields();}
    });
    paramFields.set(field.path,{field,label,control});label.append(control);details.append(label);
  }
  fieldset.append(details);
}
function refreshFields(){
  for(const [path,{field,label,control}] of paramFields){
    const state=cabinetFieldState(input,scenario,field);
    label.hidden=!state.visible;label.style.display=state.visible?'':'none';control.disabled=!state.editable;
    if(control.type==='checkbox')control.checked=input.values[path];else control.value=input.values[path]??'';
  }
}
function nestedParameters(){
  const root={};for(const [key,value] of Object.entries(input.values)){
    const names=key.split('.');let node=root;
    for(const part of names.slice(0,-1))node=node[part]??={};node[names.at(-1)]=value;
  }
  return root;
}
async function api(path,body){
  const response=await fetch(path,body===undefined?{}:{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
  const value=await response.json();if(!response.ok)throw new Error(value.error??'本地测试服务响应异常');return value;
}
function calculate(revision,parameters,bridge){
  const requestId=crypto.randomUUID();
  return new Promise((resolve,reject)=>{
    pending.set(requestId,{resolve,reject});worker.postMessage({requestId,sessionId,revision,scenario,modelName:'面对面常规虚拟测试',parameters,bridge,checkClearance:!!bridge});
  });
}
worker.onmessage=({data})=>{
  if(data.type==='ready'){workerReady();return;}
  if(data.type==='fatal'){fail(new Error(data.error));return;}
  const request=pending.get(data.requestId);if(!request)return;pending.delete(data.requestId);
  if(data.ok)request.resolve(data);else request.reject(new Error(data.error??'几何生成失败'));
};
worker.onerror=event=>fail(new Error(event.message||'几何线程未能启动'));
function fail(error){message.className='fail';message.textContent=error.message;toast(error.message);window.bridgeTestResult={error:error.message};}
async function generate(){
  if(isBusy)return;isBusy=true;el('generate').disabled=true;el('settingsButton').disabled=true;fieldset.inert=true;
  message.className='';message.textContent='正在生成柜体及读取母线接口…';
  const current=++revision,parameters=nestedParameters(),firstPreview=!displayed;
  try{
    const cabinet=await calculate(current,parameters,null);
    if(firstPreview){setScene(cabinet);fit('iso');}
    message.textContent='正在规划常规母线桥…';
    const bridge=await api('./api/plan',cabinet.upstream);
    message.textContent='正在生成桥体并检查净距与装配干涉…';
    const result=await calculate(current,parameters,bridge);
    setScene(result);
    if(firstPreview)fit('iso');
    const report=result.clearance,issues=bridge.planningMessages??[],assembly=report.assembly;
    message.className=report.passed&&assembly.passed&&!issues.length?'ok':'fail';
    const mm=row=>row?`${row.isLowerBound?'≥ ':''}${formatParameter(row.minimumMm)} mm`:'未测量';
    message.textContent=`装配干涉：${assembly.count} 处\n净距要求 ≥ ${report.requiredMm} mm · ${report.passed?'通过':'未通过'}\n相间：${mm(report.phases)}\n母线对金属：${mm(report.grounded)}`
      +(issues.length?'\n'+issues.join('；'):'')
      +(assembly.passed?'':'\n'+assembly.largest.map(hit=>hit.parts.join(' ↔ ')+'（'+formatParameter(hit.volumeMm3)+' mm³）').join('\n'))
      +(!report.passed?'\n最近：'+[report.phases,report.grounded].filter(row=>row.below).map(row=>row.nearest?.join(' ↔ ')).join('；'):'');
    window.bridgeTestResult={clearance:report,issues,instances:result.instances.length,categories:[...new Set(result.instances.map(item=>item.category))]};
  }catch(error){fail(error);}
  finally{isBusy=false;el('generate').disabled=false;el('settingsButton').disabled=false;fieldset.inert=false;}
}
el('generate').onclick=generate;
el('settingsButton').onclick=()=>{el('clearance').value=clearanceMm;el('settingsDialog').showModal();};
el('settingsForm').onsubmit=async event=>{
  event.preventDefault();try{
    const result=await api('./api/settings',{clearanceMm:el('clearance').valueAsNumber});
    clearanceMm=result.clearanceMm;input=editOriginalCabinetUiInput(input,scenario,'General.InsulationDistance',clearanceMm);
    el('settingsDialog').close();generate();
  }catch(error){toast(error.message);}
};
for(const button of document.querySelectorAll('[data-close]'))button.onclick=()=>el(button.dataset.close).close();
el('dimensionForm').onsubmit=event=>{
  event.preventDefault();try{
    input=editOriginalCabinetUiInput(input,scenario,activeDimension.field,el('dimensionValue').valueAsNumber);
    el('dimensionDialog').close();refreshFields();generate();
  }catch(error){toast(error.message);}
};
try{const saved=await api('./api/settings');clearanceMm=saved.clearanceMm;input=editOriginalCabinetUiInput(input,scenario,'General.InsulationDistance',clearanceMm);}catch(error){toast(error.message);}
refreshFields();

const engine=new THREE.WebGLRenderer({canvas,antialias:true,alpha:false});
engine.setPixelRatio(Math.min(devicePixelRatio,2));
const scene=new THREE.Scene();scene.background=new THREE.Color('#edf1f5');
const camera=new THREE.OrthographicCamera(-1,1,1,-1,0.01,120);camera.up.set(0,0,1);
const lamp=new THREE.DirectionalLight(0xffffff,2.15);lamp.position.set(2,-4,7);scene.add(lamp,new THREE.HemisphereLight(0xffffff,0x617386,1.9));
const world=new THREE.Group();scene.add(world);
const ray=new THREE.Raycaster();
const colors={cabinet:0xa6b3bf,'cabinet-trim':0x486571,base:0x8199a8,frame:0x648295,cover:0xbac7ce,bracket:0x92a9b3,hanger:0x8096a1,insulator:0xb86d51,pad:0x9fa9b0,beam:0xb6c2c9,roof:0xcbd4da};
const phaseColors={A:0xd3a944,B:0x498f78,C:0xbc6a51};
const categoryNames={cabinet:'柜体',base:'基座',raisedBase:'增高基座',frame:'基架',cover:'盖板',copper:'母线',insulator:'绝缘支撑',hanger:'吊架',reference:'环境参照'};
const layers=new Map(),hidden=new Set(),modelGroups=new Map();let isolateId=null;
const toPoint=point=>[point[0]/1000,-point[2]/1000,point[1]/1000];
const swToWeb=new THREE.Matrix4().set(.001,0,0,0, 0,0,-.001,0, 0,.001,0,0, 0,0,0,1);
const modelBounds=new THREE.Box3();let viewHeight=8,drawPending=false,view='iso';
const controls=new OcctCameraControls(camera,canvas,{pickPivot:(x,y)=>hit(x,y)?.point,
  getModelCenter:()=>modelBounds.getCenter(new THREE.Vector3()),onChange:draw});
const triad=createCoordinateTriad(stage);
const overlay=createDimensionOverlay(stage,id=>{selected=id;visibility();},dimension=>{
  if(!dimension.editable||isBusy)return;
  activeDimension=dimension;el('dimensionName').textContent=dimension.name;
  el('dimensionValue').value=input.values[dimension.field];el('dimensionDialog').showModal();el('dimensionValue').select();
});
function render(){
  drawPending=false;const width=stage.clientWidth,height=stage.clientHeight;if(!width||!height)return;
  engine.setSize(width,height,false);camera.left=-viewHeight*width/height/2;camera.right=-camera.left;
  camera.top=viewHeight/2;camera.bottom=-camera.top;camera.updateProjectionMatrix();camera.updateMatrixWorld(true);
  engine.render(scene,camera);triad.update(camera);
  overlay.update(camera,width,height,el('showDimensions').checked,hidden,isolateId&&selected!==isolateId?null:selected);
}
function draw(){if(!drawPending){drawPending=true;requestAnimationFrame(render);}}
function fit(direction=view){
  const visibleBounds=new THREE.Box3();
  for(const layer of layers.values())for(const {node} of layer.parts)if(node.visible)visibleBounds.expandByObject(node);
  if(!visibleBounds.isEmpty())modelBounds.copy(visibleBounds);
  if(modelBounds.isEmpty())return;view=direction;
  const center=modelBounds.getCenter(new THREE.Vector3()),size=modelBounds.getSize(new THREE.Vector3()),radius=size.length()/2;
  const vectors={iso:[1,-1,.72],front:[0,-1,0],top:[0,0,1],side:[1,0,0]};
  camera.up.set(0,direction==='top'?1:0,direction==='top'?0:1);
  camera.position.copy(center).add(new THREE.Vector3(...vectors[direction]).normalize().multiplyScalar(radius*3+1));
  camera.zoom=1;camera.far=Math.max(120,radius*6+10);camera.lookAt(center);controls.target.copy(center);
  viewHeight=Math.max(.12,2.15*radius*Math.max(1,stage.clientHeight/Math.max(1,stage.clientWidth)));draw();
}
function meshGeometry(item){
  const indices=item.triangles.slice();
  for(let index=0;index<item.quads.length;index+=4){
    const [a,b,c,d]=item.quads.slice(index,index+4);indices.push(a,b,c,a,c,d);
  }
  const geometry=new THREE.BufferGeometry();
  geometry.setAttribute('position',new THREE.Float32BufferAttribute(item.positions,3));geometry.setIndex(indices);geometry.computeVertexNormals();return geometry;
}
function lineGeometry(item){
  const positions=[];for(const index of item.edges)positions.push(...item.edgePositions.slice(index*3,index*3+3));
  const geometry=new THREE.BufferGeometry();geometry.setAttribute('position',new THREE.Float32BufferAttribute(positions,3));return geometry;
}
function setScene(result){
  for(const child of world.children){child.traverse(node=>{node.geometry?.dispose();node.material?.dispose();});}
  world.clear();layers.clear();
  const objectLabels=new Map(result.project.objects.filter(object=>!object.cabinetBinding).map(object=>[object.id,object.name]));
  for(const item of result.instances){
    const cover=item.appearanceRole==='cover', id=cover?item.designObjectId+':cover':item.designObjectId;
    const assemblyName=objectLabels.get(item.id.slice(0,item.id.lastIndexOf(':')));
    const name=cover?assemblyName+' · '+item.displayName:assemblyName??item.displayName.split(' / ').at(-1);
    const reference=item.category==='cabinet'&&(['beam','roof'].includes(item.appearanceRole)||name==='地面-1');
    if(!layers.has(id)){
      layers.set(id,{id,name,category:cover?'cover':reference?'reference':item.category,parts:[]});
      if(reference&&!displayed)hidden.add(id);
    }
    const color=phaseColors[item.phase]??colors[item.partSource?.previewMaterial]??colors[item.appearanceRole]??colors[item.category]??0x98a6b1;
    const face=new THREE.Mesh(meshGeometry(item),new THREE.MeshStandardMaterial({color,metalness:.28,roughness:.66,polygonOffset:true,polygonOffsetFactor:1,polygonOffsetUnits:1}));
    const edge=new THREE.LineSegments(lineGeometry(item),new THREE.LineBasicMaterial({color:0x38516a,transparent:true,opacity:.65}));
    const node=new THREE.Group();node.matrixAutoUpdate=false;node.matrix.multiplyMatrices(swToWeb,new THREE.Matrix4().fromArray(item.transform));
    face.userData.modelId=id;node.add(face,edge);world.add(node);layers.get(id).parts.push({node,face,edge,item});
  }
  // The instance matrix is assigned directly, so resolve it before measuring the scene.
  world.updateMatrixWorld(true);
  modelBounds.setFromObject(world);
  displayed=result;if(!layers.has(selected))selected=null;if(!layers.has(isolateId))isolateId=null;
  for(const id of [...hidden])if(!layers.has(id))hidden.delete(id);
  overlay.reset(result.dimensions.map(dimension=>{
    const lines=dimension.edges.map(([a,b])=>({start:toPoint(a),end:toPoint(b),startWitness:toPoint(a),endWitness:toPoint(b)}));
    const anchor=lines[0].start.map((value,axis)=>(value+lines[0].end[axis])/2);
    return {id:dimension.objectId,field:dimension.field,name:dimension.label.replace(/^([AB]柜)柜/,'$1'),editable:dimension.editable,
      lengthMm:dimension.value,dimensionKind:dimension.editable?'parameter':null,anchor,dimensionLines:lines};
  }));
  populateModelTree();visibility();
}
function hit(x,y){
  const bounds=canvas.getBoundingClientRect();if(!bounds.width||!bounds.height)return null;
  ray.setFromCamera(new THREE.Vector2((x-bounds.left)/bounds.width*2-1,1-(y-bounds.top)/bounds.height*2),camera);
  return ray.intersectObjects([...layers.values()].flatMap(layer=>layer.parts.filter(part=>part.node.visible).map(part=>part.face)),false)[0];
}
const isShown=id=>!hidden.has(id)&&(!isolateId||id===isolateId);
function populateModelTree(){
  const list=el('visibilityRows'),expanded=new Map([...modelGroups].map(([category,entry])=>[category,entry.group.open]));
  list.replaceChildren();modelGroups.clear();
  for(const category of Object.keys(categoryNames)){
    const groupRows=[...layers.values()].filter(layer=>layer.category===category);
    if(!groupRows.length)continue;
    const group=document.createElement('details');group.open=expanded.get(category)??category==='cabinet';
    const caption=document.createElement('summary');caption.textContent=categoryNames[category]??category;group.append(caption);
    const toggle=document.createElement('input');toggle.type='checkbox';toggle.setAttribute('aria-label','显示 '+caption.textContent);
    toggle.onclick=event=>event.stopPropagation();
    toggle.onchange=()=>{isolateId=null;for(const layer of groupRows)toggle.checked?hidden.delete(layer.id):hidden.add(layer.id);visibility();};caption.prepend(toggle);
    for(const layer of groupRows){
      const id=layer.id;
      const row=document.createElement('div');row.className='model-row';
      const box=document.createElement('input');box.type='checkbox';box.setAttribute('aria-label','显示 '+layer.name);
      box.onchange=()=>{isolateId=null;box.checked?hidden.delete(id):hidden.add(id);visibility();};row.append(box);
      const button=document.createElement('button');button.textContent=layer.name;
      button.onclick=()=>{selected=id;visibility();};row.append(button);group.append(row);
      layer.controls={box,button};
    }
    list.append(group);modelGroups.set(category,{group,toggle,groupRows});
  }
}
function visibility(){
  const mode=el('displayStyle').value;
  for(const {toggle,groupRows} of modelGroups.values()){
    toggle.checked=groupRows.every(layer=>isShown(layer.id));
    toggle.indeterminate=!toggle.checked&&groupRows.some(layer=>isShown(layer.id));
    for(const {id,parts,controls} of groupRows){
      const shown=isShown(id);
      for(const {node,face,edge,item} of parts){
        node.visible=shown;face.visible=mode!=='outline'&&!item.outlineOnly;edge.visible=mode!=='solid'||item.outlineOnly;
        face.material.emissive.setHex(id===selected?0x244064:0);
      }
      controls.box.checked=shown;
      controls.button.className=id===selected?'selected':shown?'':'hidden-part';
      controls.button.setAttribute('aria-pressed',String(id===selected));
    }
  }
  el('isolate').disabled=!selected;
  draw();
}
canvas.addEventListener('click',event=>{selected=hit(event.clientX,event.clientY)?.object.userData.modelId??null;visibility();});
canvas.addEventListener('contextmenu',event=>{
  event.preventDefault();const id=hit(event.clientX,event.clientY)?.object.userData.modelId;
  if(id){hidden.add(id);selected=null;visibility();}
});
for(const button of document.querySelectorAll('[data-view]'))button.onclick=()=>fit(button.dataset.view==='fit'?view:button.dataset.view);
for(const button of document.querySelectorAll('[data-tab]'))button.onclick=()=>{
  for(const other of document.querySelectorAll('[data-tab]')){const active=other===button;el(other.dataset.tab).hidden=!active;other.classList.toggle('active',active);}
};
el('showAll').onclick=()=>{hidden.clear();isolateId=null;visibility();};
el('isolate').onclick=()=>{isolateId=selected;hidden.delete(selected);visibility();};
el('displayStyle').onchange=visibility;el('showDimensions').onchange=draw;
new ResizeObserver(draw).observe(stage);draw();
ready.then(()=>{el('generate').disabled=false;return generate();}).catch(fail);
