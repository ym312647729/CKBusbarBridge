import {multiplyMatrix} from './geometry.mjs';
import {measureSolidSets} from './hanger-clearance.mjs';

// Exported mesh coordinates are Float32. Coplanar mating faces can differ by
// a few ten-thousandths of a millimetre after the two assemblies are placed.
const contactToleranceMm=0.001;
const hasThickness=box=>box.min.every((low,axis)=>box.max[axis]-low>contactToleranceMm);

function measureAssemblyInterference(parts,partition){
  let testedPairs=0,count=0;
  const largest=[];let pairIndex=0;
  for(let left=0;left<parts.length-1;left++)for(let right=left+1;right<parts.length;right++){
    const order=pairIndex++;if(partition&&order%partition.count!==partition.index)continue;
    const first=parts[left],second=parts[right];
    if(first.box.min.some((min,axis)=>Math.min(first.box.max[axis],second.box.max[axis])-Math.max(min,second.box.min[axis])<=contactToleranceMm))continue;
    testedPairs++;
    const common=first.solid.intersect(second.solid);
    try{
      if(common.status()!=='NoError')throw new Error('装配干涉求交失败：'+first.name+' / '+second.name);
      if(common.isEmpty())continue;
      const box=common.boundingBox(),volumeMm3=common.volume();
      if(!Number.isFinite(volumeMm3))throw new Error('装配干涉体积无效：'+first.name+' / '+second.name);
      if(!hasThickness(box)||Math.abs(volumeMm3)<=common.surfaceArea()*common.tolerance())continue;
      if(volumeMm3<0)throw new Error('装配干涉求得负体积：'+first.name+' / '+second.name);
      count++;
      largest.push({parts:[first.name,second.name],volumeMm3,order});
      largest.sort((a,b)=>b.volumeMm3-a.volumeMm3||a.order-b.order);
      if(largest.length>5)largest.pop();
    }finally{common.delete();}
  }
  return {passed:count===0,count,largest,solids:parts.length,testedPairs,contactToleranceMm};
}

// Check the solid meshes delivered by the very same geometry revision.
// Insulators and their live pads are intentional contacts, not grounded metal.
export function measureFixedStructureClearance(kernel,objects,results,required,partition){
  if(!Number.isFinite(required)||required<=0)throw new Error('绝缘净距应为有限正数');
  const copper=[],metal=[],parts=[],owned=[];
  try{
    for(const object of objects)for(const mesh of results.get(object.id).meshes){
      const source=mesh.partSource??{},phase=object.phase??source.phase;
      const grounded=['base','raisedBase','frame','hanger'].includes(object.category)||
        (object.cabinetBinding&&['cabinet','cabinet-trim','support'].includes(source.appearanceRole))||
        source.previewMaterial==='bracket';
      const vertices=mesh.outlinePositions,indices=mesh.outlineTriangles;
      if(!vertices?.length||!indices?.length)throw new Error('绝缘净距检查缺少原始实体网格：'+object.name);
      const original=new kernel.Manifold(new kernel.Mesh({numProp:3,vertProperties:vertices,triVerts:indices}));
      let solid;
      try{solid=original.transform(source.transform?multiplyMatrix(object.transform,source.transform):object.transform);}
      finally{original.delete();}
      owned.push(solid);
      if(solid.status()!=='NoError'||solid.isEmpty())throw new Error('绝缘净距检查的实体无效：'+object.name);
      const labels=[...(source.componentChain??[]),source.partName].filter(Boolean);
      const row={name:[object.name,...labels].join(' / '),phase,solid,box:solid.boundingBox()};
      parts.push(row);
      if(phase)copper.push(row);
      if(grounded)metal.push(row);
    }
    if(new Set(copper.map(row=>row.phase)).size!==3||!metal.length)
      throw new Error('绝缘净距检查缺少三相母线或金属结构');
    function measure(pairs){
      const search=Math.max(300,required+1);let minimum=search,nearest=null,below=0,checked=0,nearestOrder=Infinity;
      for(let order=0;order<pairs.length;order++){
        if(partition&&order%partition.count!==partition.index)continue;
        const [a,b]=pairs[order];checked++;
        const distance=measureSolidSets([a.solid],[b.solid],required,search).gapMm;
        if(distance<minimum){minimum=distance;nearest=[a.name,b.name];nearestOrder=order;}
        if(distance+1e-5<required)below++;
      }
      return {minimumMm:minimum,isLowerBound:nearest===null,nearest,below,pairs:checked,nearestOrder};
    }
    const phases=measure(copper.flatMap((first,index)=>copper.slice(index+1).filter(other=>first.phase!==other.phase).map(other=>[first,other])));
    const grounded=measure(copper.flatMap(first=>metal.map(other=>[first,other])));
    const result={requiredMm:required,passed:!phases.below&&!grounded.below,phases,grounded,assembly:measureAssemblyInterference(parts,partition),
      scope:'三相母线相间、母线对柜体和桥体金属结构；排除同相搭接和绝缘支撑接触'};
    return partition?result:mergeClearancePartitions([result]);
  }finally{owned.forEach(solid=>solid.delete());}
}

export function mergeClearancePartitions(partitions){
  if(!partitions.length||partitions.some(row=>row.requiredMm!==partitions[0].requiredMm))
    throw new Error('并行绝缘校验分区不完整或绝缘距离不一致');
  const merge=key=>{
    const rows=partitions.map(row=>row[key]),nearest=[...rows].sort((a,b)=>a.minimumMm-b.minimumMm||a.nearestOrder-b.nearestOrder)[0];
    const {nearestOrder,...result}=nearest;
    return {...result,below:rows.reduce((sum,row)=>sum+row.below,0),pairs:rows.reduce((sum,row)=>sum+row.pairs,0)};
  };
  const phases=merge('phases'),grounded=merge('grounded'),assemblyRows=partitions.map(row=>row.assembly);
  const count=assemblyRows.reduce((sum,row)=>sum+row.count,0);
  const largest=assemblyRows.flatMap(row=>row.largest).sort((a,b)=>b.volumeMm3-a.volumeMm3||a.order-b.order)
    .slice(0,5).map(({order,...row})=>row);
  return {...partitions[0],passed:!phases.below&&!grounded.below,phases,grounded,
    assembly:{...assemblyRows[0],passed:count===0,count,largest,testedPairs:assemblyRows.reduce((sum,row)=>sum+row.testedPairs,0)}};
}
