import {DefinitionError} from './expressions.mjs';

// Millimetres in the existing model's local axes. These are preview templates;
// the assembly and copper planners continue to own all mating datums.
export const fixedStructureVersion='bridge-structure-b/1';
const board=2;
const range=(box,axis)=>box.max[axis]-box.min[axis];
export function makeBlock(kernel,min,max){
  const size=max.map((value,axis)=>value-min[axis]);
  if(size.some(value=>!Number.isFinite(value)||value<=0))throw new DefinitionError('结构模板的外形尺寸无效');
  const solid=kernel.Manifold.cube(size,false);
  try{return solid.translate(min);}finally{solid.delete();}
}
function emit(kernel,run){
  const solids=[],solidSources=[];
  const append=(name,solid,appearanceRole)=>{solids.push(solid);solidSources.push({path:fixedStructureVersion,componentChain:[name],appearanceRole,bodyIndex:solids.length-1});};
  const block=(name,min,max,role)=>append(name,makeBlock(kernel,min,max),role);
  try{run(block,append);return {solids,solidSources,parameters:[],parameterMs:0,deferred:[],schematic:[],preview:{triangleCount:solids.reduce((count,solid)=>count+solid.numTri(),0),complexFeatureCount:0},previewDisplayMode:'solid'};}
  catch(error){solids.forEach(solid=>solid.delete());throw error;}
}
function unionBounds(solids){
  if(!solids.length)throw new DefinitionError('缺少参数模板的名义外形');
  const boxes=solids.map(solid=>solid.boundingBox());
  return {min:[0,1,2].map(axis=>Math.min(...boxes.map(box=>box.min[axis]))),max:[0,1,2].map(axis=>Math.max(...boxes.map(box=>box.max[axis])))};
}
function plateAt(kernel,solids,min,max){
  const slab=makeBlock(kernel,min,max),panels=[];
  try{
    for(const source of solids){const piece=source.intersect(slab);if(piece.isEmpty())piece.delete();else panels.push(piece);}
    return panels.splice(0);
  }finally{slab.delete();panels.forEach(panel=>panel.delete());}
}
// The old reference feature is used solely as a cutter boundary on the roof
// plate. The cabinet carcass and the three separate doors are authored here.
export function cabinetTemplate(kernel,datumSolids,faceSign){
  const box=unionBounds(datumSolids),{min:[x,y,z],max:[X,Y,Z]}=box;
  const depth=X-x,height=Y-y,width=Z-z,t=Math.min(board,depth/30,height/30,width/30);
  const front=faceSign>0?X:x,back=faceSign>0?x:X;
  const doorSpan=(a,b)=>[front-faceSign*a,front-faceSign*b].sort((m,n)=>m-n);
  const result=emit(kernel,(block,append)=>{
    const rail=Math.min(24,height*.04),gap=Math.min(4,height*.012),inset=Math.min(15,width*.06);
    block('左侧板',[x,y+t,z],[X,Y-t,z+t],'cabinet');
    block('右侧板',[x,y+t,Z-t],[X,Y-t,Z],'cabinet');
    block('柜底板',[x,y,z],[X,y+t,Z],'cabinet');
    const [b0,b1]=[back,back+faceSign*t].sort((m,n)=>m-n);
    block('后封板',[b0,y+t,z+t],[b1,Y-t,Z-t],'cabinet');
    for(const panel of plateAt(kernel,datumSolids,[x-t,Y-t,z-t],[X+t,Y+t,Z+t]))append('柜顶接口板',panel,'cabinet');
    const [door0,door1]=doorSpan(1,4),[handle0,handle1]=doorSpan(0,1);
    const levels=[y+rail,y+height*.28,y+height*.65,Y-rail];
    for(const [index,name] of ['电缆室门','手车室门','仪表门'].entries()){
      const lo=levels[index]+gap,hi=levels[index+1]-gap;
      block(name,[door0,lo,z+inset],[door1,hi,Z-inset],'cabinet');
      const mid=(lo+hi)/2;
      block(name+'把手',[handle0,mid-(hi-lo)*.13,Z-inset-width*.058],[handle1,mid+(hi-lo)*.13,Z-inset-width*.036],'cabinet-trim');
      if(index===2)block('仪表面板',[handle0,mid,z+width*.22],[handle1,mid+(hi-lo)*.28,z+width*.49],'cabinet-trim');
    }
    block('顶部饰板',[door0,Y-rail,z],[door1,Y-t,Z],'cabinet-trim');
  });
  for(const source of result.solidSources){
    source.partName=source.componentChain[0];
    source.componentChain=[];
  }
  return result;
}

export function baseTemplate(kernel,box,openingHeight,port){
  const {min:[x,y,z],max:[X,Y,Z]}=box,t=Math.min(board,range(box,0)/25,range(box,1)/25,range(box,2)/25),e=3*t;
  return emit(kernel,block=>{
    block('左侧折边板',[x,y,z],[X,Y,z+t],'base');
    block('右侧折边板',[x,y,Z-t],[X,Y,Z],'base');
    for(const at of [y,Y-t]) {
      for(const side of [z,Z-e])block('法兰长边',[x,at,side],[X,at+t,side+e],'base');
      // The bridge-facing side is open. No solid crosses the incoming copper.
      block('法兰后边',[x,at,z+e],[x+e,at+t,Z-e],'base');
    }
    const isHighPort=port&&port[0]>(x+X)/2, back=isHighPort?[x,x+t]:[X-t,X];
    block('检修背板',[back[0],y,z+t],[back[1],Y,Z-t],'base');
    if(port&&Number.isFinite(openingHeight)){
      const face=isHighPort?[X-t,X]:[x,x+t],lo=Math.max(y,port[1]-openingHeight/2),hi=Math.min(Y,port[1]+openingHeight/2);
      if(lo>y+1e-6)block('侧法兰下沿',[face[0],y,z+t],[face[1],lo,Z-t],'base');
      if(hi<Y-1e-6)block('侧法兰上沿',[face[0],hi,z+t],[face[1],Y,Z-t],'base');
    }
  });
}

export function frameTemplate(kernel,{min:[x,y,z],max:[X,Y,Z]},mounts=[]){
  // Keep the structural bars in the outer belt; do not consume the opening.
  const beam=Math.min(16,(X-x)/8,(Y-y)/8,(Z-z)/8);
  return emit(kernel,block=>{
    for(const height of [y,Y-beam]){
      for(const side of [z,Z-beam])block('纵梁',[x,height,side],[X,height+beam,side+beam],'frame');
      for(const end of [x,X-beam])block('端框横梁',[end,height,z+beam],[end+beam,height+beam,Z-beam],'frame');
    }
    for(const end of [x,X-beam])for(const side of [z,Z-beam])
      block('角柱',[end,y+beam,side],[end+beam,Y-beam,side+beam],'frame');
    for(const {point:[station,level],suspended} of mounts){
      const half=Math.min(beam/2,station-x,X-station);
      const bottom=suspended?level:level-beam,top=suspended?level+beam:level;
      if(half<=0||bottom<y||top>Y)continue;
      block('绝缘支架安装横梁',[station-half,bottom,z+beam],[station+half,top,Z-beam],'frame');
      for(const side of [z,Z-beam]){
        if(!suspended&&bottom>y)block('安装横梁接板',[station-half,y,side],[station+half,top,side+beam],'frame');
        if(suspended&&top<Y)block('安装横梁接板',[station-half,bottom,side],[station+half,Y,side+beam],'frame');
      }
    }
    for(const end of [x,X-2*beam])for(const vertical of [y,Y-beam])for(const side of [z,Z-beam/5])
      block('端板',[end,vertical,side],[end+2*beam,vertical+beam,side+beam/5],'frame');
  });
}

export function supportRailTemplate(kernel,bracket,layout,lBracket){
  const {min:[x,y,z],max:[X,Y,Z]}=bracket;
  const thickness=Math.min(3,(Y-y)/4,(X-x)/7,(Z-z)/16);
  return emit(kernel,block=>{
    block('横担上板',[x,Y-thickness,z],[X,Y,Z],'bracket');
    for(const side of [x,X-thickness])block('横担折边',[side,y,z],[side+thickness,Y-thickness,Z],'bracket');
    for(const end of [z,Z-thickness])block('横担端板',[x,y,end],[X,Y-thickness,end+thickness],'bracket');
    if(lBracket)for(const station of layout.groupLocalZ)
      block('侧装垫板',[x,Y-thickness,Math.max(z,station-10)],[x+thickness,Y,Math.min(Z,station+10)],'bracket');
  });
}

function cylindricalLayer(kernel,y,depth,r1,r2,segments){
  const cylinder=kernel.Manifold.cylinder(depth,r1,r2,segments),rotated=cylinder.rotate([-90,0,0]);
  try{return rotated.translate([0,y,0]);}finally{rotated.delete();cylinder.delete();}
}
export function ribbedInsulator(kernel,bounds,height,segments){
  const cx=(bounds.min[0]+bounds.max[0])/2,cz=(bounds.min[2]+bounds.max[2])/2,y=bounds.min[1];
  // Keep the installed circular end faces and their axial height. The skirts
  // are new repeated ribs within the same radial envelope.
  const radius=Math.min(range(bounds,0),range(bounds,2))/2,neck=.67*radius,end=.09*height,
    ribCount=8,step=(height-2*end)/ribCount,ribThickness=.32*step;
  if(!(height>0&&radius>0))throw new DefinitionError('绝缘子模板外形无效');
  const layers=[cylindricalLayer(kernel,y,height,neck,neck,segments)];
  try{
    layers.push(cylindricalLayer(kernel,y,end,radius,radius,segments),
      cylindricalLayer(kernel,y+height-end,end,radius,radius,segments));
    for(let index=0;index<ribCount;index++)layers.push(cylindricalLayer(kernel,y+end+(index+.26)*step,ribThickness,radius,radius,segments));
    const profile=kernel.Manifold.union(layers),holeRadius=Math.min(5,radius*.18);
    const topInsert=cylindricalLayer(kernel,y+height-end*.65,end*.7,holeRadius,holeRadius,segments);
    const bottomInsert=cylindricalLayer(kernel,y-end*.05,end*.7,holeRadius,holeRadius,segments);
    try{
      const counterbore=kernel.Manifold.union([topInsert,bottomInsert]),body=profile.subtract(counterbore);
      try{return body.translate([cx,0,cz]);}finally{body.delete();counterbore.delete();}
    }finally{profile.delete();topInsert.delete();bottomInsert.delete();}
  }finally{layers.forEach(layer=>layer.delete());}
}

export function hangerTemplate(kernel,width,height,datum){
  const W=width/2+100,r=7,t=12;
  if(![W,height].every(value=>Number.isFinite(value)&&value>2*t))throw new DefinitionError('吊架外形尺寸无效');
  return emit(kernel,(block,append)=>{
    function piece(name,min,max){
      const solid=makeBlock(kernel,min,max);
      try{append(name,solid.transform(datum),'hanger');}finally{solid.delete();}
    }
    piece('下横担',[-25,-t,-W],[25,0,W]);
    piece('顶连梁',[-30,height-t,-W],[30,height,W]);
    for(const z of [-W+25,W-25]){
      const rod=cylindricalLayer(kernel,0,height-t,r,r,20),positioned=rod.translate([0,0,z]);
      try{append('吊杆',positioned.transform(datum),'hanger');}finally{positioned.delete();rod.delete();}
      piece('连接件',[-11,height-2*t,z-11],[11,height-t,z+11]);
    }
  });
}

export function applyFixedStructureParts(result,parts){
  const previous=result.solids;
  Object.assign(result,parts);
  previous.forEach(solid=>solid.delete());
  return result;
}
export {unionBounds};
