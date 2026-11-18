import {DefinitionError} from './expressions.mjs';

// 套管的金属嵌件和绝缘本体分别占据实体空间；保留原嵌件加工几何。
export function separateBushingInserts(kernel,configuration,tracker,solids){
  const owner=configuration.previewInsulationOwner;
  if(!owner)return null;
  if(!tracker||tracker.bodies.length!==solids.length)throw new DefinitionError('套管缺少原实体归属');
  const insulation=tracker.bodies.map((body,index)=>body.owners.has(owner)?index:-1).filter(index=>index>=0);
  const inserts=solids.filter((_,index)=>!insulation.includes(index));
  if(insulation.length!==1||!inserts.length)throw new DefinitionError('套管绝缘本体或金属嵌件身份不完整');
  const cutter=kernel.Manifold.union(inserts);
  try{
    for(const index of insulation){
      const separated=solids[index].subtract(cutter);
      if(separated.status()!=='NoError'||separated.isEmpty()){separated.delete();throw new DefinitionError('套管嵌件让位失败');}
      solids[index].delete();solids[index]=separated;
    }
    return solids.map((_,index)=>({componentChain:[],bodyIndex:index,
      previewMaterial:insulation.includes(index)?'insulation':'live-terminal'}));
  }finally{cutter.delete();}
}

// 安装板保留实际套管的贯通口；安装螺栓孔由钢架删孔规则另行处理。
export function cutBushingMountPassages(kernel,configuration,solids,sources,settings){
  const passages=configuration.previewBushingPassages;
  if(!passages)return;
  const plate=configuration.components.find(row=>row.id===passages.plateComponentId);
  const indices=sources.map((source,index)=>source.componentChain[0]===plate?.name?index:-1).filter(index=>index>=0);
  if(indices.length!==1||!passages.ports.length)throw new DefinitionError('套管安装板通口身份不完整');
  const index=indices[0],box=solids[index].boundingBox(),center=box.min.map((value,axis)=>(value+box.max[axis])/2);
  const diagonal=Math.hypot(...box.max.map((value,axis)=>value-box.min[axis])),cutters=[];
  try{
    for(const {point,normal,radius}of passages.ports){
      const n=normal.map(value=>value/Math.hypot(...normal));
      const reference=Math.abs(n[0])<.9?[1,0,0]:[0,1,0];
      const cross=(a,b)=>[a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]];
      const raw=cross(reference,n),u=raw.map(value=>value/Math.hypot(...raw)),v=cross(n,u);
      const length=2*(Math.abs(center.reduce((sum,value,axis)=>sum+(value-point[axis])*n[axis],0))+diagonal+1);
      const cylinder=kernel.Manifold.cylinder(length,radius,radius,settings.arcSegments??48,false);
      try{cutters.push(cylinder.transform([...u,0,...v,0,...n,0,...point.map((value,axis)=>value-n[axis]*length/2),1]));}
      finally{cylinder.delete();}
    }
    const tool=kernel.Manifold.union(cutters);
    try{
      const opened=solids[index].subtract(tool);
      if(opened.status()!=='NoError'||opened.isEmpty()){opened.delete();throw new DefinitionError('套管安装板通口生成失败');}
      solids[index].delete();solids[index]=opened;sources[index].previewMaterial='bracket';delete sources[index].localMesh;
    }finally{tool.delete();}
  }finally{cutters.forEach(solid=>solid.delete());}
}
