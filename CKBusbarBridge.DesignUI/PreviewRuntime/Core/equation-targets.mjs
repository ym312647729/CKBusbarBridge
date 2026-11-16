import {DefinitionError,solveEquations} from './expressions.mjs';
import {equationInitialValues,resolveDimensionReference} from './equation-context.mjs';

// Bind a governing assembly equation to the actual source document/configuration/dimension.
// This produces a drive specification; it grants no editable geometry by itself.
export async function planAssemblyDimensionDrives(template,configurationName,overrides,loadTemplate){
  const configuration=template.configurations.find(row=>row.name===configurationName);
  if(!configuration?.components.length)throw new DefinitionError('尺寸驱动规划需要原始装配配置');
  const equations=configuration.equations.filter(row=>!row.disabled&&row.userDisposition?.mode!=='void-by-user'),assignments=equations.map(row=>{const assignment=/^\s*"([^"]+)"\s*=/.exec(row.expression);if(!assignment)throw new DefinitionError('无效的源装配方程');return {equation:row,target:assignment[1]};});
  const globals=new Set(assignments.filter(row=>row.equation.globalVariable&&!row.target.includes('@')).map(row=>row.target));
  for(const [name,value]of Object.entries(overrides))if(!globals.has(name)||!Number.isFinite(value)||value<=0)throw new DefinitionError('装配驱动必须是原始正值全局尺寸：'+name);
  const context=await equationInitialValues(template,configuration,loadTemplate),evaluated=solveEquations(equations,context.initialValues,overrides),targets=[],documents=new Map();
  for(const assignment of assignments){
    if(!assignment.target.includes('@'))continue;
    const resolved=await resolveDimensionReference(template,configuration,assignment.target,loadTemplate),value=evaluated[assignment.target];
    if(!Number.isFinite(value)||value<=0||resolved.dimensions.some(row=>row.dimensionType!==0||![1,2].includes(row.drivenState)))throw new DefinitionError('装配方程目标没有可验证的正长度尺寸：'+assignment.target);
    const sourceIdentity=JSON.stringify([resolved.template.source.path,resolved.configuration.name,resolved.dimension]),previous=documents.get(sourceIdentity);
    if(previous&&Math.abs(previous.value-value)>1e-7)throw new DefinitionError('同一源文档配置的尺寸被不同实例方程驱动为不同值：'+assignment.target);
    const target={reference:assignment.target,equationIndex:assignment.equation.index,instancePath:resolved.instancePath,templatePath:resolved.template.source.path,configuration:resolved.configuration.name,dimension:resolved.dimension,sourceValue:resolved.value,value,changed:Math.abs(value-resolved.value)>1e-7,sourceFeatures:[...new Set(resolved.dimensions.map(row=>row.featureName))],nativeDimensionStates:resolved.dimensions.map(row=>({readOnly:row.readOnly,drivenState:row.drivenState,dimensionType:row.dimensionType})),source:'captured-governing-assembly-equation'};
    targets.push(target);if(!previous)documents.set(sourceIdentity,{...target,references:[assignment.target]});else previous.references.push(assignment.target);
  }
  return {templatePath:template.source.path,configuration:configurationName,globals:Object.fromEntries([...globals].map(name=>[name,evaluated[name]])),targets,documentDrives:[...documents.values()],geometrySolved:false,pending:['受驱草图及后续特征求解','装配配合与坐标系更新']};
}
