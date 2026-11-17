import {DefinitionError,solveEquations} from './expressions.mjs';

export async function cabinetSpacingDimension({source,scenario,wallRear,value,installation,geometryDisplacement,cabinetComponentChains}){
  const wall=scenario==='face-to-wall',global=wall?(wallRear?'柜后距墙距离':'柜前距墙距离'):'柜间距A';
  const name=wall?'General.CabinetToWallDistance':'General.CabinetSpacing',label=wall?(wallRear?'柜后距墙距离':'柜前距墙距离'):'柜间距(A)';
  // Use the two faces of the direct distance mate, including their current shape and assembly displacement.
  const drives=source.equations.filter(row=>!row.disabled&&!row.globalVariable&&row.expression.split('=')[1]?.trim()==='"'+global+'"');
  const featureName=drives.length===1?/^\s*"[^"@]+@([^"@]+)"\s*=/.exec(drives[0].expression)?.[1]:null;
  const feature=source.features.find(row=>row.name===featureName&&!row.suppressed&&row.mateDefinition?.type===5);
  const entities=feature?.mateDefinition.entities,solvedDistance=installation.evidence.find(row=>row.feature===featureName)?.drivenSignedDistance;
  if(entities?.length!==2||!Number.isFinite(solvedDistance)||!Number.isFinite(value))throw new DefinitionError(label+'缺少已求解的面间距离配合');
  const components=entities.map(entity=>source.components.find(row=>row.name===entity.component.split('/')[0]));
  const chains=components.map(component=>cabinetComponentChains.get(component?.id)),cabinetIndex=chains.findIndex(Boolean);
  if(cabinetIndex<0||(!wall&&chains.some(chain=>!chain)))throw new DefinitionError(label+'未对应已核验的柜体实例');
  const points=await Promise.all(entities.map(async(entity,index)=>{
    const translation=installation.translations.find(row=>row.componentId===components[index]?.id);
    if(!translation||!entity.reference?.planeParameters)throw new DefinitionError(label+'缺少已定位的实体面');
    const geometry=await geometryDisplacement(entity);
    return entity.parameters.slice(0,3).map((coordinate,axis)=>coordinate*1000+translation.delta[axis]+geometry[axis]);
  }));
  const normal=entities[0].parameters.slice(3,6),length=Math.hypot(...normal),direction=normal.map(coordinate=>coordinate/length);
  const distance=direction.reduce((sum,coordinate,axis)=>sum+coordinate*(points[1][axis]-points[0][axis]),0);
  if(!Number.isFinite(distance)||Math.abs(distance-solvedDistance)>1e-4||Math.abs(Math.abs(distance)-value)>1e-4)
    throw new DefinitionError(label+'标注与实际配合距离不一致');
  // In a wall scene start on the cabinet, rather than at the unrelated height of the stored wall point.
  const opposite=1-cabinetIndex,span=distance*(cabinetIndex===0?1:-1);
  const start=wall?points[cabinetIndex]:points[cabinetIndex].map((coordinate,axis)=>(coordinate+points[opposite][axis]-direction[axis]*span)/2);
  const end=start.map((coordinate,axis)=>coordinate+direction[axis]*span);
  return {name,label,value,editable:true,annotationComponentChain:chains[cabinetIndex],annotationEdges:[[start,end]],source:'driven-cabinet-distance-mate-faces'};
}

// The offset is measured from the original eccentricity mate's zero position.
// It is not a distance between mesh boxes or a guessed cabinet centre.
export async function cabinetOffsetDimension({source,scenario,value,globals,initialValues,solved,installation,geometryDisplacement,cabinetComponentChains}){
  const wall=scenario==='face-to-wall',global=wall?'柜对墙偏心距离':'柜间偏心值',name=wall?'General.WallCabinetEccentricity':'General.CabinetEccentricity';
  const equations=source.equations.filter(row=>!row.disabled&&!row.globalVariable&&row.expression.slice(row.expression.indexOf('=')+1).includes('"'+global+'"'));
  if(equations.length!==1)throw new DefinitionError('偏心尺寸缺少唯一原驱动方程');
  const reference=/^\s*"([^"]+)"\s*=/.exec(equations[0].expression)?.[1],featureName=reference?.split('@')[1],features=source.features.filter(row=>row.name===featureName&&!row.suppressed&&row.mateDefinition?.type===5);
  if(features.length!==1)throw new DefinitionError('偏心尺寸缺少唯一原距离配合');
  const feature=features[0],entities=feature.mateDefinition.entities,rootIndex=entities.findIndex(entity=>entity.componentId===-1);
  if(entities.length!==2||rootIndex<0||entities[1-rootIndex].componentId===-1)throw new DefinitionError('偏心尺寸未对应根基准与原柜体实例');
  const movingIndex=1-rootIndex,moving=entities[movingIndex],component=source.components.find(row=>row.name.split('/').at(-1)===moving.component.split('/')[0]),translation=installation.translations.find(row=>row.componentId===component?.id),mate=installation.evidence.find(row=>row.feature===featureName);
  const annotationComponentChain=cabinetComponentChains.get(component?.id);
  if(!translation||!Number.isFinite(mate?.sourceSignedDistance))throw new DefinitionError('偏心尺寸缺少已求解的原配合位置');
  if(!annotationComponentChain)throw new DefinitionError('偏心尺寸的原配合未引用已核验的柜体实例');
  const zero=solveEquations(source.equations,initialValues,{...globals,[global]:0}),delta=(movingIndex===1?1:-1)*Math.sign(mate.sourceSignedDistance)*(solved[reference]-zero[reference]),normal=entities[0].parameters.slice(3,6),length=Math.hypot(...normal),geometry=await geometryDisplacement(moving);
  if(!Number.isFinite(delta)||!Number.isFinite(value)||length===0||Math.abs(Math.abs(delta)-Math.abs(value))>1)throw new DefinitionError('偏心标注未通过原驱动尺寸核验');
  const end=moving.parameters.slice(0,3).map((coordinate,axis)=>Math.fround(coordinate*1000+translation.delta[axis]+geometry[axis])),start=end.map((coordinate,axis)=>Math.fround(coordinate-normal[axis]/length*delta));
  return {name,label:wall?'整体参数-墙柜偏心值':'整体参数-柜间偏心值',value:Math.fround(value),editable:true,annotationComponentChain,annotationEdges:[[start,end]],source:'original-eccentricity-mate-and-zero-drive-position'};
}
