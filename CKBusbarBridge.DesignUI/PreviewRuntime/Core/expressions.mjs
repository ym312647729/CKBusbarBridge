// Deliberately restricted SW equation grammar: no eval, JavaScript or filesystem access.
const mathFunctions={abs:Math.abs,sqr:Math.sqrt,sqrt:Math.sqrt,sin:Math.sin,cos:Math.cos,tan:Math.tan,asin:Math.asin,acos:Math.acos,atan:Math.atan,atn:Math.atan,exp:Math.exp,log:Math.log,int:Math.trunc,min:Math.min,max:Math.max,round:Math.round};
export class DefinitionError extends Error {
  constructor(message,source={}){super(message);this.name='DefinitionError';this.source=source;}
}
export function parseExpression(expression){
  // SW uses an apostrophe for comments outside quoted parameter references.
  let insideReference=false;
  for(let commentIndex=0;commentIndex<expression.length;commentIndex++){
    if(expression[commentIndex]==='"')insideReference=!insideReference;
    else if(expression[commentIndex]==="'"&&!insideReference){expression=expression.slice(0,commentIndex);break;}
  }
  const tokens=[];let position=0;
  while(position<expression.length){
    if(/\s/.test(expression[position])){position++;continue;}
    const fragment=expression.slice(position);let tokenMatch;
    if((tokenMatch=/^"([^"]+)"/.exec(fragment)))tokens.push({kind:'reference',value:tokenMatch[1]});
    else if((tokenMatch=/^(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?/i.exec(fragment)))tokens.push({kind:'number',value:Number(tokenMatch[0])});
    else if((tokenMatch=/^[A-Za-z_][A-Za-z_\d]*/.exec(fragment)))tokens.push({kind:'word',value:tokenMatch[0].toLowerCase()});
    else if((tokenMatch=/^(?:<=|>=|<>|!=|==|[+*/^(),=<>-])/.exec(fragment)))tokens.push({kind:tokenMatch[0],value:tokenMatch[0]});
    else throw new DefinitionError('无法解析方程字符：'+fragment.slice(0,30));
    position+=tokenMatch[0].length;
  }
  let cursor=0;
  const precedence={'=':1,'==':1,'<>':1,'!=':1,'<':1,'>':1,'<=':1,'>=':1,'+':2,'-':2,'*':3,'/':3,'^':4};
  function parse(minimum=0){
    const token=tokens[cursor++];if(!token)throw new DefinitionError('方程缺少操作数');let node;
    if(token.kind==='number')node={type:'number',value:token.value};
    else if(token.kind==='reference')node=['suppressed','unsuppressed'].includes(token.value.toLowerCase())?{type:'suppression',value:token.value.toLowerCase()}:{type:'reference',name:token.value};
    else if(token.kind==='-'||token.kind==='+')node={type:'unary',operator:token.kind,operand:parse(4)};
    else if(token.kind==='('){node=parse();if(tokens[cursor++]?.kind!==')')throw new DefinitionError('方程缺少右括号');}
    else if(token.kind==='word'){
      if(tokens[cursor]?.kind==='('){
        cursor++;const args=[];if(tokens[cursor]?.kind!==')'){do{args.push(parse());if(tokens[cursor]?.kind!==',')break;cursor++;}while(true);}
        if(tokens[cursor++]?.kind!==')')throw new DefinitionError('函数缺少右括号');node={type:'call',name:token.value,args};
      }else if(token.value==='pi')node={type:'number',value:Math.PI};
      else throw new DefinitionError('不支持的标识符：'+token.value);
    }else throw new DefinitionError('方程操作数错误');
    while(cursor<tokens.length){
      const next=tokens[cursor];
      if(next.kind==='word'&&['mm','m','cm','deg','rad','in','ft'].includes(next.value)){
        cursor++;node={type:'binary',operator:'*',left:node,right:{type:'number',value:{mm:1,m:1000,cm:10,deg:Math.PI/180,rad:1,in:25.4,ft:304.8}[next.value]}};continue;
      }
      const rank=precedence[next.kind];if(rank===undefined||rank<minimum)break;
      cursor++;node={type:'binary',operator:next.kind,left:node,right:parse(rank+(next.kind==='^'?0:1))};
    }
    return node;
  }
  const result=parse();if(cursor!==tokens.length)throw new DefinitionError('方程包含多余内容');return result;
}
export function evaluateExpression(node,resolve){
  if(node.type==='number')return node.value;
  if(node.type==='suppression')return node.value;
  if(node.type==='reference')return resolve(node.name);
  if(node.type==='unary'){const operand=evaluateExpression(node.operand,resolve);return node.operator==='-'?-operand:operand;}
  if(node.type==='call'){
    if(node.name==='iif'||node.name==='if'){
      if(node.args.length!==3)throw new DefinitionError('IIF需要三个参数');return evaluateExpression(node.args[evaluateExpression(node.args[0],resolve)?1:2],resolve);
    }
    const fn=mathFunctions[node.name];if(!fn)throw new DefinitionError('不支持的方程函数：'+node.name);
    return fn(...node.args.map(argument=>evaluateExpression(argument,resolve)));
  }
  const left=evaluateExpression(node.left,resolve),right=evaluateExpression(node.right,resolve);
  switch(node.operator){case '+':return left+right;case '-':return left-right;case '*':return left*right;case '/':if(right===0)throw new DefinitionError('方程除数为零');return left/right;case '^':return left**right;case '=':case '==':return Number(left===right);case '!=':case '<>':return Number(left!==right);case '>':return Number(left>right);case '<':return Number(left<right);case '>=':return Number(left>=right);case '<=':return Number(left<=right);default:throw new DefinitionError('不支持的运算符');}
}
export function solveEquations(equations,initialValues={},overrides={}){
  const expressions=new Map(),resolved={...initialValues,...overrides},pending=new Set();
  for(const equation of equations){
    if(equation.disabled||equation.userDisposition?.mode==='void-by-user')continue;
    const text=typeof equation==='string'?equation:equation.expression;
    const assignment=/^\s*"([^"]+)"\s*=\s*(.*)$/.exec(text);
    if(!assignment)throw new DefinitionError('无效的源方程：'+text);
    if(expressions.has(assignment[1]))throw new DefinitionError('源方程重复定义：'+assignment[1]);
    expressions.set(assignment[1],parseExpression(assignment[2]));
    if(!Object.hasOwn(overrides,assignment[1]))delete resolved[assignment[1]];
  }
  function resolve(name){
    if(Object.hasOwn(resolved,name))return resolved[name];
    if(pending.has(name))throw new DefinitionError('方程循环引用：'+[...pending,name].join(' → '));
    if(!expressions.has(name))throw new DefinitionError('方程引用缺失：'+name);
    pending.add(name);const result=evaluateExpression(expressions.get(name),resolve);pending.delete(name);
    if(!Number.isFinite(result)&&!['suppressed','unsuppressed'].includes(result))throw new DefinitionError('方程结果非有限数：'+name);
    resolved[name]=result;return result;
  }
  for(const name of expressions.keys())resolve(name);
  return resolved;
}
