import {DefinitionError} from './expressions.mjs';

// 角钢端部按另一根实际安装角钢修配，保留原配合位置及焊接接触面。
export function fitWallWeldJoint(kernel,configuration,solids,sources){
  const joint=configuration.previewWeldJoint;if(!joint)return;
  const target=configuration.components.find(row=>row.id===joint.targetComponentId);
  const tool=configuration.components.find(row=>row.id===joint.toolComponentId);
  const targets=sources.map((source,index)=>source.componentChain[0]===target?.name?index:-1).filter(index=>index>=0);
  const tools=solids.filter((_,index)=>sources[index].componentChain[0]===tool?.name);
  if(targets.length!==1||tools.length!==1)throw new DefinitionError('穿墙钢架角钢修配身份不完整');
  const index=targets[0],trimmed=solids[index].subtract(tools[0]);
  if(trimmed.status()!=='NoError'||trimmed.isEmpty()){trimmed.delete();throw new DefinitionError('穿墙钢架角钢端部修配失败');}
  solids[index].delete();solids[index]=trimmed;
  delete sources[index].localMesh;
}
