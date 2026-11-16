import {DefinitionError} from './expressions.mjs';
import {selectRoofComponentName} from './hanger-planning.mjs';
import {collectTemplatePlanes,transformDirection} from './template-planes.mjs';
import {swMatrix,transformPoint} from './geometry.mjs';

// The original hanger assembler selects the named beam first, then the roof.
// Inspect every analytic face of that component in the driven cabinet context.
export async function collectOriginalRoofPlanes(derived,configurationName){
  const root=derived.template.configurations.find(row=>row.name===configurationName),name=selectRoofComponentName(root.components.filter(row=>row.suppression!==0).map(row=>row.name)),matches=root.components.filter(row=>row.name===name);
  if(matches.length!==1)throw new DefinitionError('原吊杆屋面组件未唯一对应');
  const component=matches[0],child=await derived.loadTemplate(component.path),metadata=await collectTemplatePlanes(child,component.configuration,derived.loadTemplate),placement=swMatrix(component.transform),identity={id:component.id,name:component.name,path:component.path,configuration:component.configuration};
  if(!metadata.complete)return metadata;
  return {...metadata,selectedComponent:identity,planes:metadata.planes.map(plane=>({...plane,key:JSON.stringify([identity.id,identity.name,plane.key]),componentChain:[identity,...plane.componentChain],point:transformPoint(plane.point,placement),normal:transformDirection(plane.normal,placement)}))};
}
