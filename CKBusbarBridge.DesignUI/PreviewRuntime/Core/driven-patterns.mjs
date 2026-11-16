import {DefinitionError} from './expressions.mjs';
const close=(first,second)=>first?.length===second?.length&&first.every((value,index)=>Math.abs(value-second[index])<1e-8);
const canonical=name=>name.split('@').slice(0,-1).join('@');

// Recover the direction convention from the captured grid before regenerating
// it. Length dimensions are millimetres; native count dimensions are unitless.
export function driveLinearPattern(feature,values,{singleInstanceDirections={},nativeInnerAxis=null}={}){
  const properties=feature.definition?.properties;
  if(feature.type!=='LPattern'||!properties||properties.VarySketch||properties.InstancesToVary)throw new DefinitionError('受驱线性阵列不是固定原网格');
  const sourceCounts=[properties.D1TotalInstances,Math.max(1,properties.D2TotalInstances)],sourceSpacing=[properties.D1Spacing,properties.D2Spacing];
  const changed=feature.dimensions.filter(row=>Object.hasOwn(values,canonical(row.name)));
  const counts=[...sourceCounts],spacing=[...sourceSpacing];
  for(const dimension of changed){
    const name=canonical(dimension.name),value=values[name],number=/^D([1-4])@/.exec(name)?.[1];
    if(!number||!Number.isFinite(value))throw new DefinitionError('阵列驱动没有原始D1至D4尺寸');
    const index=Number(number)-1;
    if(index<2){if(dimension.dimensionType!==2||!Number.isInteger(value)||value<1)throw new DefinitionError('阵列数量必须是原始整数尺寸');counts[index]=value;}
    else{if(dimension.dimensionType!==0)throw new DefinitionError('阵列间距必须是原始长度尺寸：'+feature.name+'/'+name);spacing[index-2]=value/1000;}
  }
  // Preserve the original equation's signed displacement. For example the
  // side-mounted base's 140+(L-717)/2 can be -10 mm. Apply that displacement
  // along the captured axis; do not clamp it or change the original equation.
  const recover=innerAxis=>{
  const directions=[1,2].map((index,axis)=>{
    const reference=properties['D'+index+'Axis'],direction=reference?.lineParameters?.slice(3,6)??reference?.referenceAxisParameters?.slice(3,6);
    if(sourceCounts[axis]>1){
      const step=properties.instanceTransforms?.[axis===innerAxis?1:sourceCounts[innerAxis]],first=properties.instanceTransforms?.[0];
      if(!step||!first||!(sourceSpacing[axis]>0))throw new DefinitionError('阵列方向缺少原始相邻实例');
      const measured=[0,1,2].map(coordinate=>(step[9+coordinate]-first[9+coordinate])/sourceSpacing[axis]);
      if(Math.abs(Math.hypot(...measured)-1)>1e-7||direction&&Math.abs(Math.abs(direction.reduce((sum,value,coordinate)=>sum+value*measured[coordinate],0))-1)>1e-7)throw new DefinitionError('原实例方向与捕获的阵列轴不一致');
      return measured;
    }
    if(counts[axis]===1)return [0,0,0];
    const supplied=singleInstanceDirections[index];if(supplied&&supplied.length===3&&supplied.every(Number.isFinite)&&Math.abs(Math.hypot(...supplied)-1)<1e-7&&(!direction||Math.abs(Math.abs(direction.reduce((sum,value,coordinate)=>sum+value*supplied[coordinate],0))-1)<1e-7))return [...supplied];
    throw new DefinitionError('单实例方向缺少非零原始实例，不能推定新增阵列方向');
  });
  const transforms=(gridCounts,gridSpacing)=>Array.from({length:gridCounts[0]*gridCounts[1]},(_,index)=>{const stations=[];stations[innerAxis]=index%gridCounts[innerAxis];stations[1-innerAxis]=Math.floor(index/gridCounts[innerAxis]);return [1,0,0,0,1,0,0,0,1,...[0,1,2].map(axis=>directions[0][axis]*stations[0]*gridSpacing[0]+directions[1][axis]*stations[1]*gridSpacing[1]),1,0,0,0];});
  const baseline=transforms(sourceCounts,sourceSpacing);
  if(!properties.instanceTransforms||baseline.length!==properties.instanceTransforms.length||baseline.some((matrix,index)=>!close(matrix,properties.instanceTransforms[index])))throw new DefinitionError('阵列网格不能复现原始实例变换：'+feature.name);
  return {transforms,innerAxis};
  };
  const candidates=[];for(const innerAxis of [0,1])try{if(nativeInnerAxis===null||innerAxis+1===nativeInnerAxis)candidates.push(recover(innerAxis));}catch{}
  if(!candidates.length)throw new DefinitionError('原阵列的轴方向、间距及实例顺序无法联合复现：'+feature.name);
  const selected=candidates[0],updatedTransforms=selected.transforms(counts,spacing);
  if(candidates.some(candidate=>{const alternative=candidate.transforms(counts,spacing);return alternative.length!==updatedTransforms.length||alternative.some((matrix,index)=>!close(matrix,updatedTransforms[index]));}))throw new DefinitionError('原阵列的实例顺序存在无法消除的歧义：'+feature.name);
  if(properties.SkippedItemArray?.length&&counts.some((count,index)=>count!==sourceCounts[index]))throw new DefinitionError('带跳过项的阵列数量变化尚未定义原索引迁移');
  const driven=structuredClone(feature),updated=driven.definition.properties;
  updated.D1TotalInstances=counts[0];updated.D2TotalInstances=properties.D2TotalInstances===0&&counts[1]===1?0:counts[1];updated.D1Spacing=spacing[0];updated.D2Spacing=spacing[1];updated.instanceTransforms=updatedTransforms;
  for(const dimension of driven.dimensions){const name=canonical(dimension.name);if(Object.hasOwn(values,name))dimension.value=dimension.dimensionType===2?values[name]:values[name]/1000;}
  return {feature:driven,evidence:{source:'native-linear-pattern-grid',sourceCounts,counts,spacingMm:spacing.map(value=>value*1000),nativeInnerAxis:selected.innerAxis+1}};
}
