import {DefinitionError} from './expressions.mjs';
const dot=(left,right)=>left.reduce((sum,value,index)=>sum+value*right[index],0);
export function validateInstallationMatrix(matrix){
  if(!Array.isArray(matrix)||matrix.length!==16||!matrix.every(Number.isFinite)||[3,7,11].some(index=>Math.abs(matrix[index])>1e-8)||Math.abs(matrix[15]-1)>1e-8)throw new DefinitionError('对象安装矩阵无效');
  const axes=[matrix.slice(0,3),matrix.slice(4,7),matrix.slice(8,11)];
  if(axes.some(axis=>Math.abs(dot(axis,axis)-1)>1e-6)||Math.abs(dot(axes[0],axes[1]))>1e-6||Math.abs(dot(axes[0],axes[2]))>1e-6||Math.abs(dot(axes[1],axes[2]))>1e-6)throw new DefinitionError('安装矩阵包含缩放或剪切，当前只支持刚性安装');
  const cross=[axes[0][1]*axes[1][2]-axes[0][2]*axes[1][1],axes[0][2]*axes[1][0]-axes[0][0]*axes[1][2],axes[0][0]*axes[1][1]-axes[0][1]*axes[1][0]];
  if(dot(cross,axes[2])<1-1e-6)throw new DefinitionError('安装矩阵不能包含反射');
}
export function installationPose(matrix){
  validateInstallationMatrix(matrix);
  const y=Math.asin(Math.max(-1,Math.min(1,-matrix[2]))),regular=Math.abs(Math.cos(y))>1e-7;
  const x=regular?Math.atan2(matrix[6],matrix[10]):Math.atan2(Math.sign(y)*matrix[4],matrix[5]),z=regular?Math.atan2(matrix[1],matrix[0]):0;
  return {translation:matrix.slice(12,15),rotationDegrees:[x,y,z].map(angle=>angle*180/Math.PI)};
}
export function installationMatrix({translation,rotationDegrees}){
  if(!Array.isArray(translation)||!Array.isArray(rotationDegrees)||translation.length!==3||rotationDegrees.length!==3||![...translation,...rotationDegrees].every(Number.isFinite))throw new DefinitionError('安装位置和角度必须为完整有限数值');
  const [x,y,z]=rotationDegrees.map(angle=>angle*Math.PI/180),sx=Math.sin(x),cx=Math.cos(x),sy=Math.sin(y),cy=Math.cos(y),sz=Math.sin(z),cz=Math.cos(z);
  return [cz*cy,sz*cy,-sy,0,cz*sy*sx-sz*cx,sz*sy*sx+cz*cx,cy*sx,0,cz*sy*cx+sz*sx,sz*sy*cx-cz*sx,cy*cx,0,...translation,1];
}
