import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {mkdtemp,readFile,rmdir,unlink,writeFile} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {Worker,parentPort,workerData} from 'node:worker_threads';

const runtime=new URL('../CKBusbarBridge.DesignUI/PreviewRuntime/',import.meta.url);
const launcher=fileURLToPath(new URL('../CKBusbarBridge.WebTest/bin/Debug/net48/CKBusbarBridge.WebTest.exe',import.meta.url));

function assertClosedTriangles({displayName,positions,triangles}){
  assert.ok(triangles.length>=12*3&&triangles.length%3===0,displayName);
  const edges=new Map();
  for(let index=0;index<triangles.length;index+=3)for(let corner=0;corner<3;corner++){
    const from=triangles[index+corner],to=triangles[index+(corner+1)%3];
    assert.ok(from!==to&&from>=0&&from<positions.length/3,displayName);
    const key=from<to?from+':'+to:to+':'+from,pair=edges.get(key)??[0,0];
    pair[from<to?0:1]++;edges.set(key,pair);
  }
  assert.ok([...edges.values()].every(([forward,reverse])=>forward===1&&reverse===1),displayName+' 三角面边界未闭合');
}

function worldPoints({positions,transform}){
  const points=[];
  for(let index=0;index<positions.length;index+=3)points.push([0,1,2].map(axis=>transform[12+axis]+
    transform[axis]*positions[index]+transform[4+axis]*positions[index+1]+transform[8+axis]*positions[index+2]));
  return points;
}
const span=(points,axis)=>[Math.min(...points.map(point=>point[axis])),Math.max(...points.map(point=>point[axis]))];
const aligned=(a,b)=>Math.abs(a-b)<.002;
function assertCasing(instances,covers,frameCount){
  const panels=covers.map(row=>worldPoints(row)).sort((a,b)=>span(a,0)[0]-span(b,0)[0]);
  for(let index=1;index<panels.length;index++){
    const previous=panels[index-1],current=panels[index],station=span(previous,0)[1];
    assert.ok(aligned(station,span(current,0)[0]),'相邻顶盖之间有间隙');
    const from=previous.filter(point=>aligned(point[0],station)),to=current.filter(point=>aligned(point[0],station));
    assert.ok(aligned(span(from,1)[1],span(to,1)[1]),'顶盖外表面未齐平');
    assert.ok(span(from,2).every((coordinate,axis)=>aligned(coordinate,span(to,2)[axis])),'侧板外表面未齐平');
  }
  const bottom=instances.filter(row=>row.displayName==='基架底板');
  assert.equal(bottom.length,frameCount);
  for(const lid of covers.filter(row=>row.category==='frame')){
    const related=instances.filter(row=>row.designObjectId===lid.designObjectId),outline=worldPoints(lid),edges=span(outline,0);
    for(const name of ['基架左侧板','基架右侧板']){
      const panel=worldPoints(related.find(row=>row.displayName===name));
      for(const station of edges){
        const wallEnd=span(panel.filter(point=>aligned(point[0],station)),2);
        const lidEnd=span(outline.filter(point=>aligned(point[0],station)),2);
        // The frame may face either assembly direction; compare absolute sheet fit.
        assert.ok(wallEnd.some(value=>lidEnd.some(border=>aligned(value,border))),name+' 没有贴合顶板侧边');
      }
      assert.ok(span(panel,2)[1]-span(panel,2)[0]>6.9,name+' 缺少百叶窗外伸');
    }
    for(const rail of related.filter(row=>row.displayName==='纵梁')){
      const railPoints=worldPoints(rail);
      for(const station of edges){
        const railBounds=span(railPoints.filter(point=>aligned(point[0],station)),2);
        const shellBounds=span(outline.filter(point=>aligned(point[0],station)),2);
        assert.ok(railBounds[0]>=shellBounds[0]+1.99&&railBounds[1]<=shellBounds[1]-1.99,'纵梁露出侧板');
      }
    }
  }
  assert.equal(instances.filter(row=>/^基座[左右]侧板$/.test(row.displayName)&&
    span(worldPoints(row),2)[1]-span(worldPoints(row),2)[0]>6.9).length,4);
}

function parameterTree(values){
  const parameters={};
  for(const [path,value] of Object.entries(values)){
    const keys=path.split('.');let target=parameters;
    for(const key of keys.slice(0,-1))target=target[key]??={};
    target[keys.at(-1)]=value;
  }
  return parameters;
}

if(workerData){
  const moduleUrl=new URL(workerData.moduleUrl);
  globalThis.fetch=async relative=>{
    try{return new Response(await readFile(new URL(relative,moduleUrl)));}
    catch(error){if(error.code!=='ENOENT')throw error;return new Response(null,{status:404});}
  };
  globalThis.Worker=class {
    constructor(relative){
      this.thread=new Worker(new URL(import.meta.url),{workerData:{moduleUrl:new URL(relative,moduleUrl).href}});
      this.thread.on('message',data=>this.onmessage?.({data}));
      this.thread.on('error',error=>this.onerror?.({message:error.message}));
    }
    postMessage(message){this.thread.postMessage(message);}
  };
  globalThis.self={postMessage:(message,transfer)=>parentPort.postMessage(message,transfer)};
  await import(moduleUrl.href);
  parentPort.on('message',data=>self.onmessage({data}));
}else{
  const scenario='face-to-face';
  const {createOriginalCabinetInput}=await import(new URL('Core/original-cabinet.mjs',runtime));
  const session=new Worker(new URL(import.meta.url),{workerData:{moduleUrl:new URL('session-worker.mjs',runtime).href}});
  function receive(match,action=()=>{}){
    return new Promise((resolve,reject)=>{
      const timeout=setTimeout(()=>finish(reject,new Error('面对面虚拟测试超时')),45000);
      const error=err=>finish(reject,err);
      const message=row=>{
        if(row.type==='fatal'||row.ok===false)finish(reject,new Error(row.error));
        else if(match(row))finish(resolve,row);
      };
      function finish(done,value){
        clearTimeout(timeout);session.off('message',message);session.off('error',error);done(value);
      }
      session.on('message',message);session.on('error',error);action();
    });
  }
  let caseFolder,requestFile;
  try{
    await receive(row=>row.type==='ready');
    caseFolder=await mkdtemp(join(tmpdir(),'ckbridge-facing-regular-'));
    requestFile=join(caseFolder,'upstream.json');
    for(const [caseIndex,[title,changes]] of [
      ['默认参数',{}],
      ['A 柜宽 1010 mm，柜间距 3600 mm',{'ACabinet.CabinetWidth':1010,'General.CabinetSpacing':3600}],
      ['A、B 柜基座同调至 1040 mm',{'ACabinet.FrontHole.BaseHeight':1040,'BCabinet.FrontHole.BaseHeight':1040}]
    ].entries()){
      const parameters=parameterTree({...createOriginalCabinetInput(scenario).values,...changes,'General.InsulationDistance':125});
      const request={sessionId:'facing-regular',revision:caseIndex+1,scenario,modelName:'面对面常规回归',parameters};
      const calculate=(stage,bridge,renderer)=>{
        const requestId=caseIndex+':'+stage;
        return receive(row=>row.requestId===requestId,()=>session.postMessage({...request,requestId,bridge,renderer}));
      };
      const draft=await calculate('cabinet');
      await writeFile(requestFile,JSON.stringify(draft.upstream),'utf8');
      const plan=spawnSync(launcher,['--plan',requestFile],{encoding:'utf8',windowsHide:true,timeout:45000,maxBuffer:16*1024*1024});
      assert.ifError(plan.error);
      assert.equal(plan.status,0,plan.stderr);
      const bridge=JSON.parse(plan.stdout);
      assert.deepEqual(bridge.planningMessages,[]);
      assert.ok(bridge.objects.some(row=>row.kind==='Frame')&&bridge.hangers.length&&bridge.routes.length);
      const {clearance,instances}=await calculate('bridge',bridge);
      for(const part of ['cabinet','base','frame','insulator','hanger','copper'])
        assert.ok(instances.some(row=>row.category===part),title+' 缺少 '+part);
      const covers=instances.filter(row=>row.appearanceRole==='cover');
      assert.equal(covers.filter(row=>row.category==='base').length,2);
      const frameCount=bridge.objects.filter(row=>row.kind==='Frame').length;
      assert.equal(covers.filter(row=>row.category==='frame').length,frameCount);
      assert.ok(covers.every(row=>row.quads.length===24&&row.triangles.length===0),'盖板应是六个矩形面的平板');
      assertCasing(instances,covers,frameCount);
      assert.equal(clearance.assembly.solids,instances.length);
      assert.ok(clearance.assembly.testedPairs>0);
      assert.equal(clearance.assembly.count,0,JSON.stringify(clearance.assembly.largest));
      assert.equal(clearance.requiredMm,125);
      assert.ok(clearance.passed,JSON.stringify(clearance));
      assert.ok(Math.min(clearance.phases.minimumMm,clearance.grounded.minimumMm)>=125-1e-5);
      if(caseIndex===0&&!process.argv.includes('--solidworks-preview')){
        const web=await calculate('webgl',bridge,'webgl');
        assert.equal(web.interactionInstances.length,0);
        assert.equal(web.instances.length,instances.length);
        const copper=web.instances.filter(row=>row.category==='copper');
        assert.ok(copper.some(row=>row.triangles.length===36&&row.edges.length===24),'直排应为六个矩形面和十二条外棱');
        assert.ok(copper.some(row=>row.triangles.length>36),'应保留真实开孔或折弯的三角网格');
        for(const part of copper)assertClosedTriangles(part);
        const nativeCopper=instances.filter(row=>row.category==='copper');
        assert.deepEqual(copper.map(row=>[row.edgePositions,row.edges]),nativeCopper.map(row=>[row.edgePositions,row.edges]));
        console.log('WebGL 与 OpenGL 铜排共用边线；'+copper.length+' 根铜排的 WebGL 三角面闭合。');
      }
      console.log(`${title}：${clearance.assembly.solids} 个实体，干涉 ${clearance.assembly.count} 处，`+
        `相间 ${clearance.phases.minimumMm.toFixed(2)} mm，母线对金属 ${clearance.grounded.minimumMm.toFixed(2)} mm`);
    }
    for(const [cabinet,height] of [['A',1060],['B',970]]){
      const input=createOriginalCabinetInput(scenario);
      input.values[cabinet+'Cabinet.FrontHole.BaseHeight']=height;
      const parameters=parameterTree(input.values);
      const draft=await receive(row=>row.requestId===cabinet,()=>session.postMessage({
        sessionId:'facing-regular',revision:cabinet==='A'?4:5,requestId:cabinet,scenario,modelName:'单侧基座高度回归',parameters
      }));
      for(const side of ['A','B']){
        const base=draft.project.objects.find(row=>row.baseBinding?.positionName===side+'柜前');
        const points=draft.instances.filter(row=>row.designObjectId===base.id).flatMap(worldPoints);
        const [bottom,top]=span(points,1);
        assert.ok(aligned(top-bottom,side===cabinet?height:1000),side+' 柜基座预览高度有误');
      }
      console.log(`独立调整 ${cabinet} 柜基座：实际预览 ${height} mm，另一侧仍为 1000 mm。`);
    }
  }finally{
    await session.terminate();
    if(requestFile)await unlink(requestFile).catch(error=>{if(error.code!=='ENOENT')throw error;});
    if(caseFolder)await rmdir(caseFolder);
  }
}
