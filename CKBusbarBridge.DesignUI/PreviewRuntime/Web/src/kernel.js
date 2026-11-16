import ManifoldModule from '../../vendor/manifold/manifold.js';

// The compiled module is shared; workers keep separate instances, memory and native handles.
export async function createKernelFromModule(compiledModule){
  if(!(compiledModule instanceof WebAssembly.Module))throw new Error('计算内核的编译模块无效');
  const kernel=await ManifoldModule({instantiateWasm(imports,onReady){const instance=new WebAssembly.Instance(compiledModule,imports);onReady(instance,compiledModule);return instance.exports;}});
  kernel.setup();return kernel;
}
