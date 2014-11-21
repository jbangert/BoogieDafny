﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Runtime.Caching;
using System.Text;
using System.Text.RegularExpressions;
using VC;

namespace Microsoft.Boogie
{

  struct CachedVerificationResultInjectorRun
  {
    public DateTime Start { get; internal set; }
    public DateTime End { get; internal set; }
    public int TransformedImplementationCount { get; internal set; }
    public int ImplementationCount { get; internal set; }
    public int SkippedImplementationCount { get; set; }
    public int LowPriorityImplementationCount { get; set; }
    public int MediumPriorityImplementationCount { get; set; }
    public int HighPriorityImplementationCount { get; set; }
    public long[] CachingActionCounts { get; set; }
  }


  sealed class CachedVerificationResultInjectorStatistics
  {
    ConcurrentDictionary<string, CachedVerificationResultInjectorRun> runs = new ConcurrentDictionary<string, CachedVerificationResultInjectorRun>();

    public bool AddRun(string requestId, CachedVerificationResultInjectorRun run)
    {
      return runs.TryAdd(requestId, run);
    }

    public string Output(bool printTime = false)
    {
      var wr = new StringWriter();
      if (runs.Any())
      {
        wr.WriteLine("Cached verification result injector statistics as CSV:");
        wr.WriteLine("Request ID, Transformed, Low, Medium, High, Skipped{0}", printTime ? ", Time (ms)" : "");
        foreach (var kv in runs.OrderBy(kv => ExecutionEngine.AutoRequestId(kv.Key)))
        {
          var t = printTime ? string.Format(", {0,8:F0}", kv.Value.End.Subtract(kv.Value.Start).TotalMilliseconds) : "";
          wr.WriteLine("{0,-19}, {1,3}, {2,3}, {3,3}, {4,3}, {5,3}{6}", kv.Key, kv.Value.TransformedImplementationCount, kv.Value.LowPriorityImplementationCount, kv.Value.MediumPriorityImplementationCount, kv.Value.HighPriorityImplementationCount, kv.Value.SkippedImplementationCount, t);
        }
      }
      return wr.ToString();
    }
  }


  sealed class CachedVerificationResultInjector : StandardVisitor
  {
    readonly IEnumerable<Implementation> Implementations;
    readonly Program Program;
    // TODO(wuestholz): We should probably increase the threshold to something like 2 seconds.
    static readonly double TimeThreshold = -1.0d;
    Program programInCachedSnapshot;
    Implementation currentImplementation;
    int assumptionVariableCount;
    int temporaryVariableCount;

    public static readonly CachedVerificationResultInjectorStatistics Statistics = new CachedVerificationResultInjectorStatistics();

    int FreshAssumptionVariableName
    {
      get
      {
        return assumptionVariableCount++;
      }
    }

    int FreshTemporaryVariableName
    {
      get
      {
        return temporaryVariableCount++;
      }
    }

    CachedVerificationResultInjector(Program program, IEnumerable<Implementation> implementations)
    {
      Implementations = implementations;
      Program = program;
    }

    public Implementation Inject(Implementation implementation, Program programInCachedSnapshot)
    {
      Contract.Requires(implementation != null && programInCachedSnapshot != null);

      this.programInCachedSnapshot = programInCachedSnapshot;
      assumptionVariableCount = 0;
      temporaryVariableCount = 0;
      currentImplementation = implementation;
      var result = VisitImplementation(implementation);
      currentImplementation = null;
      this.programInCachedSnapshot = null;
      return result;
    }

    public static void Inject(Program program, IEnumerable<Implementation> implementations, string requestId, string programId, out long[] cachingActionCounts)
    {
      var eai = new CachedVerificationResultInjector(program, implementations);

      cachingActionCounts = new long[Enum.GetNames(typeof(VC.ConditionGeneration.CachingAction)).Length];
      var run = new CachedVerificationResultInjectorRun { Start = DateTime.UtcNow, ImplementationCount = implementations.Count(), CachingActionCounts = cachingActionCounts };
      foreach (var impl in implementations)
      {
        int priority;
        var vr = ExecutionEngine.Cache.Lookup(impl, out priority);
        if (vr != null && vr.ProgramId == programId)
        {
          if (priority == Priority.LOW)
          {
            run.LowPriorityImplementationCount++;
            if (TimeThreshold < vr.End.Subtract(vr.Start).TotalMilliseconds)
            {
              SetErrorAndAssertionChecksumsInCachedSnapshot(impl, vr);
              if (vr.ProgramId != null)
              {
                var p = ExecutionEngine.CachedProgram(vr.ProgramId);
                if (p != null)
                {
                  eai.Inject(impl, p);
                  run.TransformedImplementationCount++;
                }
              }
            }
          }
          else if (priority == Priority.MEDIUM)
          {
            run.MediumPriorityImplementationCount++;
            if (TimeThreshold < vr.End.Subtract(vr.Start).TotalMilliseconds)
            {
              SetErrorAndAssertionChecksumsInCachedSnapshot(impl, vr);
              if (vr.ProgramId != null)
              {
                var p = ExecutionEngine.CachedProgram(vr.ProgramId);
                if (p != null)
                {
                  eai.Inject(impl, p);
                  run.TransformedImplementationCount++;
                }
              }
            }
          }
          else if (priority == Priority.HIGH)
          {
            run.HighPriorityImplementationCount++;
          }
          else if (priority == Priority.SKIP)
          {
            run.SkippedImplementationCount++;
          }
        }
      }
      run.End = DateTime.UtcNow;
      Statistics.AddRun(requestId, run);
    }

    private static void SetErrorAndAssertionChecksumsInCachedSnapshot(Implementation implementation, VerificationResult result)
    {
      if (result.Outcome == ConditionGeneration.Outcome.Errors && result.Errors != null && result.Errors.Count < CommandLineOptions.Clo.ProverCCLimit)
      {
        implementation.SetErrorChecksumToCachedError(result.Errors.Select(cex => new Tuple<byte[], object>(cex.Checksum, cex)));
        implementation.AssertionChecksumsInCachedSnapshot = result.AssertionChecksums;
      }
      else if (result.Outcome == ConditionGeneration.Outcome.Correct)
      {
        implementation.SetErrorChecksumToCachedError(new List<Tuple<byte[], object>>());
        implementation.AssertionChecksumsInCachedSnapshot = result.AssertionChecksums;
      }
    }

    public override Cmd VisitCallCmd(CallCmd node)
    {
      var result = base.VisitCallCmd(node);

      var oldProc = programInCachedSnapshot.FindProcedure(node.Proc.Name);
      if (oldProc != null
          && oldProc.DependencyChecksum != node.Proc.DependencyChecksum
          && node.AssignedAssumptionVariable == null)
      {
        var before = new List<Cmd>();
        var beforePrecondtionCheck = new List<Cmd>();
        var after = new List<Cmd>();
        Expr assumedExpr = new LiteralExpr(Token.NoToken, false);
        // TODO(wuestholz): Try out two alternatives: only do this for low priority implementations or not at all.
        var canUseSpecs = DependencyCollector.AllFunctionDependenciesAreDefinedAndUnchanged(oldProc, Program);
        if (canUseSpecs)
        {
          var desugaring = node.Desugaring;
          Contract.Assert(desugaring != null);
          var precond = node.CheckedPrecondition(oldProc, Program);
          if (precond != null)
          {
            var assume = new AssumeCmd(node.tok, precond, new QKeyValue(Token.NoToken, "precondition_previous_snapshot", new List<object>(), null));
            beforePrecondtionCheck.Add(assume);
          }

          assumedExpr = node.Postcondition(oldProc, Program);
          var unmods = node.UnmodifiedBefore(oldProc);
          foreach (var unmod in unmods)
          {
            var oldUnmod = new LocalVariable(Token.NoToken,
              new TypedIdent(Token.NoToken, string.Format("{0}##old##{1}", unmod.Name, FreshTemporaryVariableName), unmod.Type));
            before.Add(new AssignCmd(Token.NoToken,
                         new List<AssignLhs> { new SimpleAssignLhs(Token.NoToken, new IdentifierExpr(Token.NoToken, oldUnmod)) },
                         new List<Expr> { new IdentifierExpr(Token.NoToken, unmod.Decl) }));
            var eq = LiteralExpr.Eq(new IdentifierExpr(Token.NoToken, oldUnmod), new IdentifierExpr(Token.NoToken, unmod.Decl));
            if (assumedExpr == null)
            {
              assumedExpr = eq;
            }
            else
            {
              assumedExpr = LiteralExpr.And(assumedExpr, eq);
            }
          }
        }

        if (assumedExpr != null)
        {
          var lv = new LocalVariable(Token.NoToken,
            new TypedIdent(Token.NoToken, string.Format("a##post##{0}", FreshAssumptionVariableName), Type.Bool),
            new QKeyValue(Token.NoToken, "assumption", new List<object>(), null));
          node.AssignedAssumptionVariable = lv;
          currentImplementation.InjectAssumptionVariable(lv, !canUseSpecs);
          var lhs = new SimpleAssignLhs(Token.NoToken, new IdentifierExpr(Token.NoToken, lv));
          var rhs = LiteralExpr.And(new IdentifierExpr(Token.NoToken, lv), assumedExpr);
          var assumed = new AssignCmd(node.tok, new List<AssignLhs> { lhs }, new List<Expr> { rhs });
          after.Add(assumed);
        }

        node.ExtendDesugaring(before, beforePrecondtionCheck, after);
        if (1 <= CommandLineOptions.Clo.TraceCaching)
        {
          using (var tokTxtWr = new TokenTextWriter("<console>", Console.Out, false, false))
          {
            var loc = node.tok != null && node.tok != Token.NoToken ? string.Format("{0}({1},{2})", node.tok.filename, node.tok.line, node.tok.col) : "<unknown location>";
            Console.Out.WriteLine("Processing call to procedure {0} in implementation {1} (at {2}):", node.Proc.Name, currentImplementation.Name, loc);
            foreach (var b in before)
            {
              Console.Out.Write("  >>> added before: ");
              b.Emit(tokTxtWr, 0);
            }
            foreach (var b in beforePrecondtionCheck)
            {
              Console.Out.Write("  >>> added before precondition check: ");
              b.Emit(tokTxtWr, 0);
            }
            foreach (var a in after)
            {
              Console.Out.Write("  >>> added after: ");
              a.Emit(tokTxtWr, 0);
            }
          }
        }
      }

      return result;
    }
  }


  sealed class OtherDefinitionAxiomsCollector : ReadOnlyVisitor
  {
    Axiom currentAxiom;
    Trigger currentTrigger;

    public static void Collect(IEnumerable<Axiom> axioms)
    {
      var start = DateTime.UtcNow;

      var v = new OtherDefinitionAxiomsCollector();
      foreach (var a in axioms)
      {
        v.currentAxiom = a;
        v.VisitExpr(a.Expr);
        v.currentAxiom = null;
      }

      var end = DateTime.UtcNow;
      if (3 <= CommandLineOptions.Clo.TraceCaching)
      {
        Console.Out.WriteLine("Collected other definition axioms within {0:F0} ms.", end.Subtract(start).TotalMilliseconds);
      }
    }

    public override QuantifierExpr VisitQuantifierExpr(QuantifierExpr node)
    {
      currentTrigger = node.Triggers;
      while (currentTrigger != null)
      {
        foreach (var e in currentTrigger.Tr)
        {
          VisitExpr(e);
        }
        currentTrigger = currentTrigger.Next;
      }
      return base.VisitQuantifierExpr(node);
    }

    public override Expr VisitNAryExpr(NAryExpr node)
    {
      if (currentTrigger != null)
      {
        // We found a function call within a trigger of a quantifier expression.
        var funCall = node.Fun as FunctionCall;
        if (funCall != null && funCall.Func != null && funCall.Func.Checksum != null && funCall.Func.Checksum != "stable")
        {
          funCall.Func.AddOtherDefinitionAxiom(currentAxiom);
        }
      }
      return base.VisitNAryExpr(node);
    }
  }


  sealed class DependencyCollector : ReadOnlyVisitor
  {
    private DeclWithFormals currentDeclaration;

    public static void Collect(Program program)
    {
      var start = DateTime.UtcNow;

      var dc = new DependencyCollector();
      dc.VisitProgram(program);

      var end = DateTime.UtcNow;
      if (3 <= CommandLineOptions.Clo.TraceCaching)
      {
        Console.Out.WriteLine("Collected dependencies within {0:F0} ms.", end.Subtract(start).TotalMilliseconds);
      }
    }

    public static bool AllFunctionDependenciesAreDefinedAndUnchanged(Procedure oldProc, Program newProg)
    {
      Contract.Requires(oldProc != null && newProg != null);

      var funcs = newProg.Functions;
      return oldProc.DependenciesCollected
             && (oldProc.FunctionDependencies == null || oldProc.FunctionDependencies.All(dep => funcs.Any(f => f.Name == dep.Name && f.DependencyChecksum == dep.DependencyChecksum)));
    }

    public override Procedure VisitProcedure(Procedure node)
    {
      currentDeclaration = node;

      foreach (var param in node.InParams)
      {
        if (param.TypedIdent != null && param.TypedIdent.WhereExpr != null)
        {
          VisitExpr(param.TypedIdent.WhereExpr);
        }
      }

      var result = base.VisitProcedure(node);
      node.DependenciesCollected = true;
      currentDeclaration = null;
      return result;
    }

    public override Implementation VisitImplementation(Implementation node)
    {
      currentDeclaration = node;

      foreach (var param in node.InParams)
      {
        if (param.TypedIdent != null && param.TypedIdent.WhereExpr != null)
        {
          VisitExpr(param.TypedIdent.WhereExpr);
        }
      }

      if (node.Proc != null)
      {
        node.AddProcedureDependency(node.Proc);
      }

      var result = base.VisitImplementation(node);
      node.DependenciesCollected = true;
      currentDeclaration = null;
      return result;
    }

    public override Function VisitFunction(Function node)
    {
      currentDeclaration = node;

      if (node.DefinitionAxiom != null)
      {
        VisitAxiom(node.DefinitionAxiom);
      }
      if (node.OtherDefinitionAxioms != null)
      {
        foreach (var a in node.OtherDefinitionAxioms)
        {
          if (a != node.DefinitionAxiom)
          {
            VisitAxiom(a);
          }
        }
      }

      var result = base.VisitFunction(node);
      node.DependenciesCollected = true;
      currentDeclaration = null;
      return result;
    }

    public override Cmd VisitCallCmd(CallCmd node)
    {
      if (currentDeclaration != null)
      {
        currentDeclaration.AddProcedureDependency(node.Proc);
      }

      return base.VisitCallCmd(node);
    }

    public override Expr VisitNAryExpr(NAryExpr node)
    {
      var funCall = node.Fun as FunctionCall;
      if (funCall != null && currentDeclaration != null)
      {
        currentDeclaration.AddFunctionDependency(funCall.Func);
      }

      return base.VisitNAryExpr(node);
    }
  }


  static internal class Priority
  {
    public static readonly int LOW = 1;             // the same snapshot has been verified before, but a callee has changed
    public static readonly int MEDIUM = 2;          // old snapshot has been verified before
    public static readonly int HIGH = 3;            // has been never verified before
    public static readonly int SKIP = int.MaxValue; // highest priority to get them done as soon as possible
  }


  public sealed class VerificationResultCache
  {
    private readonly MemoryCache Cache = new MemoryCache("VerificationResultCache");
    private readonly CacheItemPolicy Policy = new CacheItemPolicy { SlidingExpiration = new TimeSpan(0, 10, 0), Priority = CacheItemPriority.Default };


    public void Insert(Implementation impl, VerificationResult result)
    {
      Contract.Requires(impl != null);
      Contract.Requires(result != null);

      Cache.Set(impl.Id, result, Policy);
    }


    public VerificationResult Lookup(Implementation impl, out int priority)
    {
      Contract.Requires(impl != null);

      var result = Cache.Get(impl.Id) as VerificationResult;
      if (result == null)
      {
        priority = Priority.HIGH;
      }
      else if (result.Checksum != impl.Checksum)
      {
        priority = Priority.MEDIUM;
      }
      else if (impl.DependencyChecksum == null || result.DependeciesChecksum != impl.DependencyChecksum)
      {
        priority = Priority.LOW;
      }
      else
      {
        priority = Priority.SKIP;
      }
      return result;
    }


    public void Clear()
    {
      Cache.Trim(100);
    }


    public void RemoveMatchingKeys(Regex keyRegexp)
    {
      Contract.Requires(keyRegexp != null);

      foreach (var kv in Cache)
      {
        if (keyRegexp.IsMatch(kv.Key))
        {
          Cache.Remove(kv.Key);
        }
      }
    }


    public int VerificationPriority(Implementation impl)
    {
      Contract.Requires(impl != null);

      int priority;
      Lookup(impl, out priority);
      return priority;
    }
  }

}
