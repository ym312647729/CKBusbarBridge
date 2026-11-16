import {DefinitionError} from './expressions.mjs';
export function dimensionLayout(start,end,{textWidth,textHeight,gapPixels,lineOffsetPixels=22,marginPixels=4}){
  const dx=end.x-start.x,dy=end.y-start.y,length=Math.hypot(dx,dy);
  if(!(length>0)||![start.x,start.y,end.x,end.y,textWidth,textHeight,gapPixels,lineOffsetPixels,marginPixels].every(Number.isFinite)||[textWidth,textHeight,gapPixels,marginPixels].some(value=>value<0))throw new DefinitionError('尺寸标注投影或字形度量无效');
  const unit={x:dx/length,y:dy/length},normal={x:-unit.y,y:unit.x};if(normal.y>0||Math.abs(normal.y)<1e-10&&normal.x>0){normal.x*=-1;normal.y*=-1;}
  const a={x:start.x+normal.x*lineOffsetPixels,y:start.y+normal.y*lineOffsetPixels},b={x:end.x+normal.x*lineOffsetPixels,y:end.y+normal.y*lineOffsetPixels},middle={x:(a.x+b.x)/2,y:(a.y+b.y)/2};
  const normalHalfSpan=(Math.abs(normal.x)*textWidth+Math.abs(normal.y)*textHeight)/2,offset=normalHalfSpan+gapPixels;
  const textCenter={x:middle.x+normal.x*offset,y:middle.y+normal.y*offset},box={x:textCenter.x-textWidth/2,y:textCenter.y-textHeight/2,width:textWidth,height:textHeight};
  const halfGap=Math.min(length/2,(Math.abs(unit.x)*textWidth+Math.abs(unit.y)*textHeight)/2+marginPixels),lineSegments=[];
  if(halfGap<length/2){lineSegments.push([a,{x:middle.x-unit.x*halfGap,y:middle.y-unit.y*halfGap}],[{x:middle.x+unit.x*halfGap,y:middle.y+unit.y*halfGap},b]);}
  return {a,b,unit,normal,middle,box,textCenter,lineSegments,length};
}
