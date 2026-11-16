export const previewSeamStitchBelowMm=.1;
const difference=(first,second)=>first.map((value,index)=>value-second[index]);
const length=vector=>Math.hypot(...vector),dot=(first,second)=>first[0]*second[0]+first[1]*second[1],cross=(first,second)=>first[0]*second[1]-first[1]*second[0];
const midpoint=(first,second)=>first.map((value,index)=>(value+second[index])/2);
const belowStitchLimit=(gap,coordinateScale=1)=>gap>1e-8&&previewSeamStitchBelowMm-gap>Number.EPSILON*Math.max(1,coordinateScale)*16;

// Only a proven straight rectangular slit is removed from a cut profile.
// Circles, curved slots, islands and larger openings retain their native shape.
export function straightPreviewSlit(loop){
  if(!Array.isArray(loop)||loop.length<4||loop.some(point=>point.length!==2||!point.every(Number.isFinite)))return null;
  let points=loop.filter((point,index)=>length(difference(point,loop[(index+loop.length-1)%loop.length]))>1e-8);
  points=points.filter((point,index)=>{const incoming=difference(point,points[(index+points.length-1)%points.length]),outgoing=difference(points[(index+1)%points.length],point);return Math.abs(cross(incoming,outgoing))>1e-8*length(incoming)*length(outgoing)||dot(incoming,outgoing)<=0;});
  if(points.length!==4)return null;
  const edges=points.map((point,index)=>difference(points[(index+1)%4],point)),lengths=edges.map(length);
  if(lengths.some(value=>value<=1e-8)||edges.some((edge,index)=>Math.abs(dot(edge,edges[(index+1)%4]))>1e-8*lengths[index]*lengths[(index+1)%4])||Math.abs(lengths[0]-lengths[2])>1e-6||Math.abs(lengths[1]-lengths[3])>1e-6)return null;
  const width=Math.min(...lengths),extent=Math.max(...lengths);
  if(!belowStitchLimit(width,Math.max(...points.flat().map(Math.abs)))||extent<previewSeamStitchBelowMm)return null;
  const cap=lengths[0]<lengths[1]?0:1;
  return {widthMm:width,lengthMm:extent,centerline:[midpoint(points[cap],points[(cap+1)%4]),midpoint(points[(cap+2)%4],points[(cap+3)%4])]};
}

export function stitchPreviewSlitGroups(groups){
  const retained=[],stitched=[];
  for(const group of groups){const slit=group.length===1?straightPreviewSlit(group[0]):null;if(slit)stitched.push(slit);else retained.push(group);}
  return {groups:retained,stitched};
}

// The original selected edge supplies the extension limit. Only a flange end
// inside that edge and less than 0.1 mm away is extended; wider relief remains.
export function stitchPreviewFlangeEnds(loops,profileLower,profileUpper,edgeLower,edgeUpper){
  const lowerGap=profileLower-edgeLower,upperGap=edgeUpper-profileUpper;
  const coordinateScale=Math.max(Math.abs(edgeLower),Math.abs(edgeUpper));
  const stitchLower=belowStitchLimit(lowerGap,coordinateScale),stitchUpper=belowStitchLimit(upperGap,coordinateScale);
  return {loops:loops.map(loop=>loop.map(([u,v])=>[stitchLower&&Math.abs(u-profileLower)<1e-7?edgeLower:stitchUpper&&Math.abs(u-profileUpper)<1e-7?edgeUpper:u,v])),lower:stitchLower?edgeLower:profileLower,upper:stitchUpper?edgeUpper:profileUpper,stitched:[...(stitchLower?[{end:'lower',gapMm:lowerGap}]:[]),...(stitchUpper?[{end:'upper',gapMm:upperGap}]:[])]};
}
