import {DefinitionError} from './expressions.mjs';

// The medium-voltage preview shows a 100 × 13 mm stamped vent with a
// 5 mm outward rise, on pitches of 340 mm horizontally and 80 mm vertically.
const ventWidth=100,ventHeight=13,ventRise=5,margin=30;

export function panelPrism(kernel,contour,thickness,placement){
  const section=new kernel.CrossSection([contour],'EvenOdd');
  try{
    const extruded=section.extrude(thickness);
    try{return extruded.transform(placement);}finally{extruded.delete();}
  }finally{section.delete();}
}

function stations(low,high,size,pitch){
  const count=Math.max(0,Math.floor((high-low-2*margin-size)/pitch+1e-9)+1);
  const first=(low+high-size-(count-1)*pitch)/2;
  return Array.from({length:count},(_,index)=>first+index*pitch);
}

// Work in a flat sheet with its outside face at z=0. All louvers bulge
// outwards and have a real downwards opening rather than an overlay.
export function ventedSidePanel(kernel,{start,end,bottom,topStart,topEnd,outerStart,outerEnd,outward,thickness}){
  const flat=panelPrism(kernel,[[start,bottom],[end,bottom],[end,topEnd],[start,topStart]],thickness,
    [1,0,0,0,0,1,0,0,0,0,1,0,0,0,-thickness,1]);
  const cutters=[],hoods=[];
  const slopes=(outerEnd-outerStart)/(end-start);
  const placement=[1,0,slopes,0,0,1,0,0,0,0,outward,0,0,0,outerStart-slopes*start,1];
  try{
    const columns=stations(start,end,ventWidth,340),rows=stations(bottom,Math.min(topStart,topEnd),ventHeight,80);
    if(!rows.length||!columns.length)return flat.transform(placement);
    const hoodProfile=[[0,ventRise],[ventHeight,0],[ventHeight,-thickness],[0,ventRise-thickness]];
    const cheekProfile=[[0,ventRise],[ventHeight,0],[ventHeight,-thickness],[0,-thickness]];
    const along=(x,y)=>[0,1,0,0,0,0,1,0,1,0,0,0,x,y,0,1];
    for(const x of columns)for(const y of rows){
      const aperture=kernel.Manifold.cube([ventWidth-2*thickness,ventHeight-thickness,thickness+2]);
      try{cutters.push(aperture.translate([x+thickness,y,-thickness-1]));}finally{aperture.delete();}
      hoods.push(panelPrism(kernel,hoodProfile,ventWidth,along(x,y)));
      hoods.push(panelPrism(kernel,cheekProfile,thickness,along(x,y)));
      hoods.push(panelPrism(kernel,cheekProfile,thickness,along(x+ventWidth-thickness,y)));
    }
    const apertures=kernel.Manifold.union(cutters);
    try{
      const perforated=flat.subtract(apertures);
      try{
        const formed=kernel.Manifold.union([perforated,...hoods]);
        try{
          if(formed.status()!=='NoError'||formed.isEmpty())throw new DefinitionError('百叶窗侧板未生成有效实体');
          return formed.transform(placement);
        }finally{formed.delete();}
      }finally{perforated.delete();}
    }finally{apertures.delete();}
  }finally{flat.delete();cutters.forEach(solid=>solid.delete());hoods.forEach(solid=>solid.delete());}
}
