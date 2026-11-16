// Explicitly scoped to an immutable template reader. Call clear() when replacing
// the definition set. Public design export uses fresh calculation unless opted in.
export class DefinitionMemo{
  constructor(maxEntries=64){this.maxEntries=maxEntries;this.clear();}
  clear(){this.owners=new WeakMap();this.hits=0;this.misses=0;}
  async run(owner,stage,input,create){
    let entries=this.owners.get(owner);if(!entries){entries=new Map();this.owners.set(owner,entries);}
    const key=JSON.stringify([stage,input]);let pending=entries.get(key);
    if(pending){this.hits++;entries.delete(key);entries.set(key,pending);}
    else{this.misses++;pending=Promise.resolve().then(create);entries.set(key,pending);if(entries.size>this.maxEntries)entries.delete(entries.keys().next().value);pending.catch(()=>{if(entries.get(key)===pending)entries.delete(key);});}
    return structuredClone(await pending);
  }
}
export const memoizeDefinition=(memo,owner,stage,input,create)=>memo?memo.run(owner,stage,input,create):create();
