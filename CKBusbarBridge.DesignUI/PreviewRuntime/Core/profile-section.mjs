import {DefinitionError} from './expressions.mjs';

// Selected regions/contours form a union. EvenOdd applies only inside each region (including its holes).
export function unionProfileSections(kernel,groups){
  let combined=null;
  try{
    for(const contours of groups){
      const section=new kernel.CrossSection(contours,'EvenOdd');
      if(!combined){combined=section;continue;}
      try{const next=combined.add(section);combined.delete();combined=next;}finally{section.delete();}
    }
    if(!combined||combined.isEmpty())throw new DefinitionError('选定截面没有有效面积');
    return combined;
  }catch(error){combined?.delete();throw error;}
}
