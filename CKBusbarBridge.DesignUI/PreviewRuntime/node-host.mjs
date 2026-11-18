import {Worker,parentPort,workerData} from 'node:worker_threads';
import {readFile} from 'node:fs/promises';
import {createInterface} from 'node:readline';
import {fileURLToPath} from 'node:url';

if(workerData){
  const moduleUrl=new URL(workerData.module);
  const children=new Set();
  globalThis.fetch=async path=>{
    const target=new URL(path,moduleUrl);
    if(target.protocol!=='file:')throw new Error('计算器只允许读取本地资源');
    try{return new Response(await readFile(target));}
    catch(error){if(error.code==='ENOENT')return new Response(null,{status:404});throw error;}
  };
  globalThis.Worker=class{
    constructor(path){
      const child=this.inner=new Worker(new URL(import.meta.url),{workerData:{module:new URL(path,moduleUrl).href}});
      children.add(child);
      child.on('message',data=>this.onmessage?.({data}));
      child.on('error',error=>this.onerror?.({message:error.message}));
      child.on('exit',()=>children.delete(child));
    }
    postMessage(value,transfer){this.inner.postMessage(value,transfer);}
    terminate(){return this.inner.terminate();}
  };
  globalThis.self={postMessage:(value,transfer)=>parentPort.postMessage(value,transfer)};
  await import(moduleUrl.href);
  parentPort.on('message',data=>self.onmessage({data}));
  parentPort.on('close',()=>{for(const child of children)child.terminate();});
}else{
  const engine=new Worker(new URL(import.meta.url),{workerData:{module:new URL('./session-worker.mjs',import.meta.url).href}});
  engine.on('message',data=>process.stdout.write(JSON.stringify(data)+'\n'));
  engine.on('error',error=>{process.stdout.write(JSON.stringify({type:'fatal',ok:false,error:error.message})+'\n');process.exitCode=1;});
  const input=createInterface({input:process.stdin,crlfDelay:Infinity});
  input.on('line',line=>{try{engine.postMessage(JSON.parse(line));}catch(error){process.stderr.write(error.message+'\n');}});
  input.on('close',()=>process.exit(0));
  const parentIndex=process.argv.indexOf('--parent'),parentId=Number(process.argv[parentIndex+1]);
  if(parentIndex>=0&&Number.isInteger(parentId)&&parentId>0)setInterval(()=>{
    try{process.kill(parentId,0);}catch{process.exit(0);}
  },1000).unref();
}
