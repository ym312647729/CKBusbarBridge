import {DefinitionError} from './expressions.mjs';

// Captured root component IDs, not display-name or spatial guesses. Propagate a root
// role through its exact component subtree; fixed outgoing copper retains its phase.
export function assignCabinetRenderRoles(configuration, scenario){
  const roles=scenario==='face-to-wall'
    ?new Map([[25,'cabinet'],[36,'roof'],[102,'beam'],[60,'support']])
    :new Map([[25,'cabinet'],[26,'cabinet'],[33,'beam'],[40,'beam']]);
  for(const [id,role] of roles){
    const matches=configuration.components.filter(component=>component.id===id);
    if(matches.length!==1)throw new DefinitionError('柜体预览角色缺少唯一原组件身份：'+id);
    matches[0].previewRole=role;
  }
}
