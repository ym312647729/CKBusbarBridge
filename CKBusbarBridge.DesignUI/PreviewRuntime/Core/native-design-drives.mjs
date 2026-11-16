import {DefinitionError,solveEquations} from './expressions.mjs';
import {equationInitialValues,resolveDimensionReference} from './equation-context.mjs';

// Design handoff data only. Values come from captured source equations and
// references; this function never executes SolidWorks or validates a model.
export async function planNativeEquationDrives(template,configuration,variables,read,{dimensionInputs={}}={}){
  const context=await equationInitialValues(template,configuration,read),equations=configuration.equations.filter(row=>!row.disabled&&row.userDisposition?.mode!=='void-by-user');
  const names=new Set(equations.filter(row=>row.globalVariable).map(row=>/^\s*"([^"]+)"\s*=/.exec(row.expression)?.[1]));
  if(Object.entries(variables).some(([name,value])=>!names.has(name)||!Number.isFinite(value)))throw new DefinitionError('设计变量没有原方程定义');
  const targets=new Set(equations.map(row=>/^\s*"([^"]+)"\s*=/.exec(row.expression)?.[1]));
  if(Object.entries(dimensionInputs).some(([name,value])=>!name.includes('@')||!Object.hasOwn(context.initialValues,name)||targets.has(name)||!Number.isFinite(value)))throw new DefinitionError('外部尺寸输入没有对应原方程引用');
  const evaluated=solveEquations(equations,{...context.initialValues,...dimensionInputs},variables),dimensions=[],documents=new Map();
  for(const equation of equations){
    const reference=/^\s*"([^"]+)"\s*=/.exec(equation.expression)?.[1];if(!reference?.includes('@'))continue;
    const resolved=await resolveDimensionReference(template,configuration,reference,read),valueMm=evaluated[reference];if(!Number.isFinite(valueMm))throw new DefinitionError('设计原方程未求值');
    const key=JSON.stringify([resolved.template.source.path,resolved.configuration.name,resolved.dimension]),previous=documents.get(key);
    if(previous!==undefined&&Math.abs(previous-valueMm)>1e-7)throw new DefinitionError('同一原文档尺寸被不同实例驱动为不同值');documents.set(key,valueMm);
    const types=new Set(resolved.dimensions.map(dimension=>dimension.dimensionType));if(types.size!==1||![0,2].includes([...types][0]))throw new DefinitionError('导出尺寸单位未支持或不一致');
    const count=types.has(2);if(count&&(!Number.isInteger(valueMm)||valueMm<1))throw new DefinitionError('原阵列数量方程未生成正整数');
    dimensions.push({reference,dimension:resolved.dimension,...(count?{value:valueMm,sourceValue:resolved.value,unit:'count'}:{valueMm,sourceValueMm:resolved.value}),templatePath:resolved.template.source.path,configuration:resolved.configuration.name,instancePath:resolved.instancePath,sourceEquation:equation.expression});
  }
  return {globalVariables:{...variables},resolvedGlobalVariables:Object.fromEntries([...names].map(name=>[name,evaluated[name]])),dimensions};
}
