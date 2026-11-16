import {multiplyMatrix} from './geometry.mjs';
import {measureSolidSets} from './hanger-clearance.mjs';

// Exported mesh coordinates are Float32. Coplanar mating faces can differ by
// a few ten-thousandths of a millimetre after the two assemblies are placed.
const contactToleranceMm=0.001;
const hasThickness=box=>box.min.every((low,axis)=>box.max[axis]-low>contactToleranceMm);

function measureAssemblyInterference(parts){
  let testedPairs=0,count=0;
  const largest=[];
  for(let left=0;left<parts.length-1;left++)for(let right=left+1;right<parts.length;right++){
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
      largest.push({parts:[first.name,second.name],volumeMm3});
      largest.sort((a,b)=>b.volumeMm3-a.volumeMm3);
      if(largest.length>5)largest.pop();
    }finally{common.delete();}
  }
  return {passed:count===0,count,largest,solids:parts.length,testedPairs,contactToleranceMm};
}

// Check the solid meshes delivered by the very same geometry revision.
// Insulators and their live pads are intentional contacts, not grounded metal.
export function measureFixedStructureClearance(kernel,objects,results,required){
  if(!Number.isFinite(required)||required<=0)throw new Error('绝缘净距应为有限正数');
  const copper=[],metal=[],parts=[],owned=[];
  try{
    for(const object of objects)for(const mesh of results.get(object.id).meshes){
      const source=mesh.partSource??{},phase=object.phase??source.phase;
      const grounded=['base','raisedBase','frame','hanger'].includes(object.category)||
        (object.cabinetBinding&&['cabinet','cabinet-trim'].includes(source.appearanceRole))||
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
      const search=Math.max(300,required+1);let minimum=search,nearest=null,below=0;
      for(const [a,b] of pairs){
        const distance=measureSolidSets([a.solid],[b.solid],required,search).gapMm;
        if(distance<minimum){minimum=distance;nearest=[a.name,b.name];}
        if(distance+1e-5<required)below++;
      }
      return {minimumMm:minimum,isLowerBound:nearest===null,nearest,below,pairs:pairs.length};
    }
    const phases=measure(copper.flatMap((first,index)=>copper.slice(index+1).filter(other=>first.phase!==other.phase).map(other=>[first,other])));
    const grounded=measure(copper.flatMap(first=>metal.map(other=>[first,other])));
    return {requiredMm:required,passed:!phases.below&&!grounded.below,phases,grounded,assembly:measureAssemblyInterference(parts),
      scope:'三相母线相间、母线对柜体和桥体金属结构；排除同相搭接和绝缘支撑接触'};
  }finally{owned.forEach(solid=>solid.delete());}
}
