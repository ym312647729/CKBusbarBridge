import {DefinitionError} from './expressions.mjs';
const dot=(left,right)=>left.reduce((sum,value,index)=>sum+value*right[index],0);
const subtract=(left,right)=>left.map((value,index)=>value-right[index]);
function vector(values){if(!Array.isArray(values)||values.length!==3||!values.every(Number.isFinite))throw new DefinitionError('拖动射线或方向无效');return [...values];}
function normalized(values){const result=vector(values),length=Math.hypot(...result);if(length<1e-10)throw new DefinitionError('拖动方向为零');return result.map(value=>value/length);}
export function freezeAxisDrag(axisOrigin,axisDirection,viewDirection){
  const origin=vector(axisOrigin),axis=normalized(axisDirection),view=normalized(viewDirection),parallel=dot(view,axis),normal=view.map((value,index)=>value-axis[index]*parallel),length=Math.hypot(...normal);
  if(length<1e-6)throw new DefinitionError('当前视角沿拖动轴，请旋转视角或编辑位置数值');
  return {origin,axis,normal:normal.map(value=>value/length)};
}
export function axisDragCoordinate(frozen,rayOrigin,rayDirection){
  const origin=vector(rayOrigin),direction=normalized(rayDirection),denominator=dot(direction,frozen.normal);
  if(Math.abs(denominator)<1e-8)throw new DefinitionError('当前射线与拖动平面平行');
  const distance=dot(subtract(frozen.origin,origin),frozen.normal)/denominator;
  if(distance<0)throw new DefinitionError('拖动平面位于观察者后方');
  return dot(subtract(origin.map((value,index)=>value+direction[index]*distance),frozen.origin),frozen.axis);
}
