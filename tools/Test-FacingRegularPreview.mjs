import assert from 'node:assert/strict';
import {spawnSync} from 'node:child_process';
import {mkdtemp,readFile,rmdir,unlink,writeFile} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
import {fileURLToPath} from 'node:url';
import {Worker,parentPort,workerData} from 'node:worker_threads';

const runtime=new URL('../CKBusbarBridge.DesignUI/PreviewRuntime/',import.meta.url);
const launcher=fileURLToPath(new URL('../CKBusbarBridge.WebTest/bin/Debug/net48/CKBusbarBridge.WebTest.exe',import.meta.url));

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
      ['A 柜宽 1010 mm，柜间距 3600 mm',{'ACabinet.CabinetWidth':1010,'General.CabinetSpacing':3600}]
    ].entries()){
      const values={...createOriginalCabinetInput(scenario).values,...changes,'General.InsulationDistance':125},parameters={};
      for(const [key,value] of Object.entries(values)){
        const keys=key.split('.');let target=parameters;
        for(const name of keys.slice(0,-1))target=target[name]??={};
        target[keys.at(-1)]=value;
      }
      const request={sessionId:'facing-regular',revision:caseIndex+1,scenario,modelName:'面对面常规回归',parameters};
      const calculate=(stage,bridge)=>{
        const requestId=caseIndex+':'+stage;
        return receive(row=>row.requestId===requestId,()=>session.postMessage({...request,requestId,bridge}));
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
      assert.equal(covers.filter(row=>row.category==='frame').length,bridge.objects.filter(row=>row.kind==='Frame').length);
      assert.ok(covers.every(row=>row.quads.length===24&&row.triangles.length===0),'盖板应是六个矩形面的平板');
      assert.equal(clearance.assembly.solids,instances.length);
      assert.ok(clearance.assembly.testedPairs>0);
      assert.equal(clearance.assembly.count,0,JSON.stringify(clearance.assembly.largest));
      assert.equal(clearance.requiredMm,125);
      assert.ok(clearance.passed,JSON.stringify(clearance));
      assert.ok(Math.min(clearance.phases.minimumMm,clearance.grounded.minimumMm)>=125-1e-5);
      console.log(`${title}：${clearance.assembly.solids} 个实体，干涉 ${clearance.assembly.count} 处，`+
        `相间 ${clearance.phases.minimumMm.toFixed(2)} mm，母线对金属 ${clearance.grounded.minimumMm.toFixed(2)} mm`);
    }
  }finally{
    await session.terminate();
    if(requestFile)await unlink(requestFile).catch(error=>{if(error.code!=='ENOENT')throw error;});
    if(caseFolder)await rmdir(caseFolder);
  }
}
