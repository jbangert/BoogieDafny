// RUN: %dafny /print:"%t.print" "%s" > "%t"
// RUN: %diff "%s.expect" "%t"

module ReadsRequiresReads {
  function MyReadsOk(f : A -> B, a : A) : set<object>
    reads f.reads(a);
  {
    f.reads(a)
  }

  function MyReadsOk2(f : A -> B, a : A) : set<object>
    reads f.reads(a);
  {
    (f.reads)(a)
  }

  function MyReadsOk3(f : A -> B, a : A) : set<object>
    reads (f.reads)(a);
  {
    f.reads(a)
  }

  function MyReadsOk4(f : A -> B, a : A) : set<object>
    reads (f.reads)(a);
  {
    (f.reads)(a)
  }

  function MyReadsBad(f : A -> B, a : A) : set<object>
  {
    f.reads(a)
  }

  function MyReadsBad2(f : A -> B, a : A) : set<object>
  {
    (f.reads)(a)
  }

  function MyReadsOk'(f : A -> B, a : A, o : object) : bool
    reads f.reads(a);
  {
    o in f.reads(a)
  }

  function MyReadsBad'(f : A -> B, a : A, o : object) : bool
  {
    o in f.reads(a)
  }

  function MyRequiresOk(f : A -> B, a : A) : bool
    reads f.reads(a);
  {
    f.requires(a)
  }

  function MyRequiresBad(f : A -> B, a : A) : bool
  {
    f.requires(a)
  }
}

module WhatWeKnowAboutReads {
  function ReadsNothing():(){()}

  lemma IndeedNothing() {
    assert ReadsNothing.reads() == {};
  }

  method NothingHere() {
    assert (() => ()).reads() == {};
  }

  class S {
    var s : S;
  }

  function ReadsSomething(s : S):()
    reads s;
  {()}

  method MaybeSomething() {
    var s  := new S;
    var s' := new S;
           if * { assert s in ReadsSomething.reads(s) || ReadsSomething.reads(s) == {};
    } else if * { assert s in ReadsSomething.reads(s);
    } else if * { assert ReadsSomething.reads(s) == {};
    } else if * { assert s' !in ReadsSomething.reads(s);
    } else if * { assert s' in ReadsSomething.reads(s);
    }
  }

  method SomethingHere() {
    var s  := new S;
    var s' := new S;
    var f := (u) reads u => ();
           if * { assert s in f.reads(s) || f.reads(s) == {};
    } else if * { assert s in f.reads(s);
    } else if * { assert f.reads(s) == {};
    } else if * { assert s' !in f.reads(s);
    } else if * { assert s' in f.reads(s);
    }
  }
}

module ReadsAll {
  function A(f: int -> int) : int
    reads set o,x | o in f.reads(x) :: o;
    requires forall x :: f.requires(x);
  {
    f(0) + f(1) + f(2)
  }

  function method B(f: int -> int) : int
    reads set o,x | o in f.reads(x) :: o;
    requires forall x :: f.requires(x);
  {
    f(0) + f(1) + f(2)
  }

  function C(f: int -> int) : int
    reads f.reads;
    requires forall x :: f.requires(x);
  {
    f(0) + f(1) + f(2)
  }

  function method D(f: int -> int) : int
    reads f.reads;
    requires forall x :: f.requires(x);
  {
    f(0) + f(1) + f(2)
  }
}
