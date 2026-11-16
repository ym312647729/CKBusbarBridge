import {DefinitionError,solveEquations} from './expressions.mjs';

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
