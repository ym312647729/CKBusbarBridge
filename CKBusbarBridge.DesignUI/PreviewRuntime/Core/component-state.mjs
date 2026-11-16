export function componentState(component){
  const visibility=component.visibility;
  return {visible:visibility?.hiddenWithoutSuppression!==true&&visibility?.visibleState!==0,envelope:visibility?.isEnvelope===true,known:Number.isInteger(visibility?.visibleState)&&typeof visibility?.hiddenWithoutSuppression==='boolean'&&typeof visibility?.isEnvelope==='boolean'};
}

export function componentMassTotals(rows){
  let visible=0,includingHidden=0,componentScopeKnown=true;
  for(const {component,result}of rows){
    const state=componentState(component);componentScopeKnown&&=state.known&&result.massComponentScopeKnown!==false;
    if(state.envelope)continue;
    const mass=result.massVolumes;if(!mass){componentScopeKnown=false;continue;}
    includingHidden+=mass.includingHidden;if(state.visible)visible+=mass.visible;
  }
  return {massVolumes:{visible,includingHidden},massComponentScopeKnown:componentScopeKnown};
}
