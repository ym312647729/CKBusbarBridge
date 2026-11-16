import earcut from '../vendor/earcut/earcut.mjs';

const cross2=(left,right)=>left[0]*right[1]-left[1]*right[0];
const minus2=(left,right)=>[left[0]-right[0],left[1]-right[1]];
const area=loop=>loop.reduce((sum,point,index)=>sum+cross2(point,loop[(index+1)%loop.length]),0)/2;
const rectangleLoop=rectangle=>[[rectangle.x0,rectangle.y0],[rectangle.x1,rectangle.y0],[rectangle.x1,rectangle.y1],[rectangle.x0,rectangle.y1]];
const overlaps=(left,right,epsilon)=>left.x0<=right.x1+epsilon&&right.x0<=left.x1+epsilon&&left.y0<=right.y1+epsilon&&right.y0<=left.y1+epsilon;
const bounds=loop=>({x0:Math.min(...loop.map(point=>point[0])),x1:Math.max(...loop.map(point=>point[0])),y0:Math.min(...loop.map(point=>point[1])),y1:Math.max(...loop.map(point=>point[1]))});

function combineFrames(frames,epsilon){
  const groups=[];
  for(const frame of frames){
    let combined={...frame,holes:[...frame.holes]},changed=true;
    while(changed){changed=false;for(let index=groups.length-1;index>=0;index--){const other=groups[index];if(!overlaps(combined,other,epsilon))continue;combined={x0:Math.min(combined.x0,other.x0),x1:Math.max(combined.x1,other.x1),y0:Math.min(combined.y0,other.y0),y1:Math.max(combined.y1,other.y1),holes:[...combined.holes,...other.holes]};groups.splice(index,1);changed=true;}}
    groups.push(combined);
  }
  return groups;
}
function subtractRectangle(rectangle,cut,epsilon){
  const x0=Math.max(rectangle.x0,cut.x0),x1=Math.min(rectangle.x1,cut.x1),y0=Math.max(rectangle.y0,cut.y0),y1=Math.min(rectangle.y1,cut.y1);
  if(x1-x0<=epsilon||y1-y0<=epsilon)return [rectangle];
  return [{x0:rectangle.x0,x1:x0,y0:rectangle.y0,y1:rectangle.y1},{x0:x1,x1:rectangle.x1,y0:rectangle.y0,y1:rectangle.y1},{x0,x1,y0:rectangle.y0,y1:y0},{x0,x1,y0:y1,y1:rectangle.y1}].filter(piece=>piece.x1-piece.x0>epsilon&&piece.y1-piece.y0>epsilon);
}
function mergeRectangles(rectangles,epsilon){
  let changed=true;
  while(changed){changed=false;outer:for(let first=0;first<rectangles.length;first++)for(let second=first+1;second<rectangles.length;second++){
    const left=rectangles[first],right=rectangles[second],same=(a,b)=>Math.abs(a-b)<=epsilon;
    const horizontal=same(left.y0,right.y0)&&same(left.y1,right.y1)&&(same(left.x1,right.x0)||same(right.x1,left.x0));
    const vertical=same(left.x0,right.x0)&&same(left.x1,right.x1)&&(same(left.y1,right.y0)||same(right.y1,left.y0));
    if(!horizontal&&!vertical)continue;
    rectangles[first]={x0:Math.min(left.x0,right.x0),x1:Math.max(left.x1,right.x1),y0:Math.min(left.y0,right.y0),y1:Math.max(left.y1,right.y1)};rectangles.splice(second,1);changed=true;break outer;
  }}
  return rectangles;
}

// This partitions an existing face only. Hole vertices and their sampling phase are never changed.
export function planarHolePatches(outer,holes,epsilon=1e-8){
  if(outer.length<4||!holes.length||[outer,...holes].some(loop=>loop.length<3||loop.some(point=>point.length!==2||!point.every(Number.isFinite))))return null;
  const rectangle=bounds(outer),rectangleArea=(rectangle.x1-rectangle.x0)*(rectangle.y1-rectangle.y0);
  if(!(rectangleArea>epsilon*epsilon)||area(outer)<=0||Math.abs(area(outer)-rectangleArea)>Math.max(rectangleArea*1e-9,epsilon*(rectangle.x1-rectangle.x0+rectangle.y1-rectangle.y0)))return null;
  if(outer.some(point=>Math.min(Math.abs(point[0]-rectangle.x0),Math.abs(point[0]-rectangle.x1),Math.abs(point[1]-rectangle.y0),Math.abs(point[1]-rectangle.y1))>epsilon))return null;
  const frames=[];
  for(const [index,hole]of holes.entries()){
    if(area(hole)>=0)return null;
    const box=bounds(hole),clearance=Math.min(box.x0-rectangle.x0,rectangle.x1-box.x1,box.y0-rectangle.y0,rectangle.y1-box.y1),minorSize=Math.min(box.x1-box.x0,box.y1-box.y0);
    if(clearance<=epsilon||minorSize<=epsilon)return null;
    // A small local margin avoids a hole touching its partition frame. It changes no material.
    const margin=Math.min(minorSize/8,clearance/2);
    frames.push({x0:box.x0-margin,x1:box.x1+margin,y0:box.y0-margin,y1:box.y1+margin,holes:[index]});
  }
  const groups=combineFrames(frames,epsilon);
  let rectangles=[rectangle];
  for(const frame of groups)rectangles=rectangles.flatMap(piece=>subtractRectangle(piece,frame,epsilon));
  rectangles=mergeRectangles(rectangles,epsilon);
  const quads=rectangles.map(rectangleLoop),triangles=[];
  for(const frame of groups){
    const loops=[rectangleLoop(frame),...frame.holes.map(index=>holes[index])],points=loops.flat(),offsets=[];let cursor=loops[0].length;
    for(const loop of loops.slice(1)){offsets.push(cursor);cursor+=loop.length;}
    const indices=earcut(points.flat(),offsets,2);
    for(let index=0;index<indices.length;index+=3){const triangle=indices.slice(index,index+3).map(vertex=>points[vertex]);if(area(triangle)<0)triangle.reverse();if(area(triangle)>epsilon*epsilon)triangles.push(triangle);}
  }
  const expectedArea=rectangleArea+holes.reduce((sum,hole)=>sum+area(hole),0),actualArea=quads.reduce((sum,quad)=>sum+area(quad),0)+triangles.reduce((sum,triangle)=>sum+area(triangle),0);
  if(!(expectedArea>0)||Math.abs(actualArea-expectedArea)>Math.max(expectedArea*1e-9,epsilon*epsilon*100))return null;
  return {quads,triangles,frames:groups.map(({holes,...frame})=>({...frame,holeCount:holes.length})),area:actualArea,holeCount:holes.length};
}

export function simplifiedBoundary(loop,epsilon){
  let result=loop.map(point=>[...point]),changed=true;
  while(changed&&result.length>3){changed=false;for(let index=0;index<result.length;index++){const previous=result[(index+result.length-1)%result.length],current=result[index],next=result[(index+1)%result.length],a=minus2(current,previous),b=minus2(next,current);if(Math.abs(cross2(a,b))<=epsilon*(Math.hypot(...a)+Math.hypot(...b))&&a[0]*b[0]+a[1]*b[1]>=0){result.splice(index,1);changed=true;break;}}}
  return result;
}
